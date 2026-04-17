using System;
using System.Collections.Generic;
using Localsend.Backend.Protocol;
using Localsend.Backend.Util;

namespace Localsend.Backend.Receiver
{
    /// <summary>单活动会话。LocalSend v1 协议规定同一时刻只允许一个会话。</summary>
    internal sealed class Session : EventArgs
    {
        public string Id;
        public DeviceInfo Sender;
        public Dictionary<string, FileDto> Files;          // fileId → metadata
        public Dictionary<string, string> Tokens;          // fileId → token
        public Dictionary<string, bool> Completed;         // fileId → true 当完成
        public DateTime StartedUtc;
        public DateTime LastActivityUtc;

        public bool AllDone
        {
            get
            {
                foreach (KeyValuePair<string, FileDto> kv in Files)
                {
                    bool done;
                    if (!Completed.TryGetValue(kv.Key, out done) || !done) return false;
                }
                return true;
            }
        }
    }

    public enum FileDecision { Accept, Reject }

    /// <summary>用户策略：对收到的 send-request 决定接受或拒绝（整体/单文件）。</summary>
    public interface IReceivePolicy
    {
        /// <summary>对整个请求给出接受/拒绝；若接受，可为每个 fileId 指定是否接受。</summary>
        /// <returns>key = fileId，value = 决定；不在字典中的 fileId 视为拒绝。</returns>
        Dictionary<string, FileDecision> Decide(string senderAlias, List<string> fileNames);
    }

    /// <summary>默认策略：自动接受全部。用于开发期；发布后应替换为 UI 弹窗策略。</summary>
    internal sealed class AutoAcceptPolicy : IReceivePolicy
    {
        public Dictionary<string, FileDecision> Decide(string senderAlias, List<string> fileNames)
        {
            Dictionary<string, FileDecision> r = new Dictionary<string, FileDecision>();
            return r; // 调用方在没给出具体决定时会按全部接受处理（见 SessionManager）
        }
    }

    internal sealed class SessionManager
    {
        private readonly object _lock = new object();
        private Session _current;

        /// <summary>空闲或可启动新会话。</summary>
        public bool IsIdle { get { lock (_lock) { return _current == null; } } }

        /// <summary>
        /// 尝试开启会话。
        /// 返回每个**接受的** fileId → token 的映射；拒绝的 fileId 不在结果中。
        /// 若返回 null，表示当前有其他会话在进行（调用方应回 409）。
        /// </summary>
        public Dictionary<string, string> TryBegin(DeviceInfo sender, Dictionary<string, FileDto> files, IReceivePolicy policy)
        {
            lock (_lock)
            {
                if (_current != null) return null;

                Dictionary<string, FileDecision> decisions = null;
                if (policy != null)
                {
                    List<string> names = new List<string>();
                    foreach (FileDto f in files.Values) names.Add(f.FileName);
                    decisions = policy.Decide(sender != null ? sender.Alias : "unknown", names);
                }

                Session s = new Session();
                s.Id = IdGen.NewRandom();
                s.Sender = sender;
                s.Files = new Dictionary<string, FileDto>();
                s.Tokens = new Dictionary<string, string>();
                s.Completed = new Dictionary<string, bool>();
                s.StartedUtc = DateTime.UtcNow;
                s.LastActivityUtc = s.StartedUtc;

                foreach (KeyValuePair<string, FileDto> kv in files)
                {
                    bool accept = true;
                    if (decisions != null && decisions.Count > 0)
                    {
                        FileDecision d;
                        accept = decisions.TryGetValue(kv.Key, out d) && d == FileDecision.Accept;
                    }
                    if (!accept) continue;
                    s.Files[kv.Key] = kv.Value;
                    s.Tokens[kv.Key] = IdGen.NewRandom();
                    s.Completed[kv.Key] = false;
                }

                if (s.Files.Count == 0) return new Dictionary<string, string>(); // 全拒绝：不占用会话

                _current = s;
                Log.Info("Session started: sender=" + (sender != null ? sender.Alias : "?") + " files=" + s.Files.Count);
                return new Dictionary<string, string>(s.Tokens);
            }
        }

        /// <summary>校验 fileId/token 对，返回文件元数据（或 null 失败）。</summary>
        public FileDto ValidateUpload(string fileId, string token)
        {
            lock (_lock)
            {
                if (_current == null) return null;
                string expect;
                if (!_current.Tokens.TryGetValue(fileId, out expect)) return null;
                if (expect != token) return null;
                _current.LastActivityUtc = DateTime.UtcNow;
                return _current.Files[fileId];
            }
        }

        /// <summary>标记单文件完成；若全部完成则关闭会话并触发事件。</summary>
        public void MarkCompleted(string fileId)
        {
            bool finished = false;
            Session done = null;
            lock (_lock)
            {
                if (_current == null) return;
                _current.Completed[fileId] = true;
                _current.LastActivityUtc = DateTime.UtcNow;
                if (_current.AllDone) { done = _current; _current = null; finished = true; }
            }
            if (finished)
            {
                Log.Info("Session completed: " + done.Id);
                EventHandler<Session> h = SessionCompleted;
                if (h != null) try { h(this, done); } catch { }
            }
        }

        public void Cancel()
        {
            Session c;
            lock (_lock) { c = _current; _current = null; }
            if (c != null) Log.Info("Session cancelled: " + c.Id);
        }

        /// <summary>检查超时并清理。由外部定时调用。</summary>
        public void Tick()
        {
            lock (_lock)
            {
                if (_current == null) return;
                TimeSpan idle = DateTime.UtcNow - _current.LastActivityUtc;
                if (idle.TotalMilliseconds > Constants.SessionIdleTimeoutMs)
                {
                    Log.Warn("Session timed out: " + _current.Id);
                    _current = null;
                }
            }
        }

        public event EventHandler<Session> SessionCompleted;
    }
}
