using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Localsend.Backend.Sender
{
    /// <summary>
    /// 放行自签证书的 ICertificatePolicy。CF 3.5 上 ServicePointManager 没有
    /// ServerCertificateValidationCallback，但保留了旧的 ICertificatePolicy 接口。
    /// </summary>
    internal sealed class AllowAnyCertPolicy : ICertificatePolicy
    {
        public bool CheckValidationResult(ServicePoint sp, X509Certificate cert, WebRequest req, int problem)
        {
            return true;
        }

        /// <summary>安装到全局；幂等。</summary>
        public static void Install()
        {
            try { ServicePointManager.CertificatePolicy = new AllowAnyCertPolicy(); }
            catch { /* 某些环境下只读，忽略 */ }
        }
    }
}
