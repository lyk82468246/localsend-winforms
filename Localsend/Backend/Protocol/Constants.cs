using System;

namespace Localsend.Backend.Protocol
{
    /// <summary>LocalSend 协议常量。参见项目根 PROTOCOL.md。</summary>
    internal static class Constants
    {
        public const string MulticastGroup = "224.0.0.167";
        public const int MulticastPort = 53317;
        public const int RestPort = 53317;

        public const string DefaultAlias = "WM6 Device";
        public const string DeviceModel = "Windows Mobile 6";
        public const string DeviceType = "mobile";
        public const string ProtocolScheme = "http";
        public const string ProtocolVersion = "2.0";

        public const string ApiV1 = "/api/localsend/v1";
        public const string ApiV2 = "/api/localsend/v2";

        public const int SessionIdleTimeoutMs = 5 * 60 * 1000;
    }
}
