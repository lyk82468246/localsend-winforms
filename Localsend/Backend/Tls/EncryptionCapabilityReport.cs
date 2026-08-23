using System;

namespace Localsend.Backend.Tls
{
    public enum EncryptionCapabilityLevel
    {
        Unavailable = 0,
        ReceiveOnly = 1,
        Full = 2
    }

    /// <summary>
    /// Result of a local TLS capability probe. ReasonCode is stable and is
    /// translated by the UI; Detail is diagnostic text and must not be used as
    /// a localization key.
    /// </summary>
    public sealed class EncryptionCapabilityReport
    {
        public EncryptionCapabilityLevel Level { get; private set; }
        public string Provider { get; private set; }
        public string ReasonCode { get; private set; }
        public string Detail { get; private set; }
        public DateTime TestedAtUtc { get; private set; }

        public EncryptionCapabilityReport(
            EncryptionCapabilityLevel level,
            string provider,
            string reasonCode,
            string detail)
        {
            Level = level;
            Provider = provider ?? "none";
            ReasonCode = string.IsNullOrEmpty(reasonCode) ? "tls.unknown" : reasonCode;
            Detail = detail ?? "";
            TestedAtUtc = DateTime.UtcNow;
        }

        public static EncryptionCapabilityReport NotProbed()
        {
            return new EncryptionCapabilityReport(
                EncryptionCapabilityLevel.Unavailable,
                "none",
                "tls.notProbed",
                "");
        }
    }
}
