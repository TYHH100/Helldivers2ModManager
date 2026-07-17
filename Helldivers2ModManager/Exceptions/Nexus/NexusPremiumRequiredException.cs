namespace Helldivers2ModManager.Exceptions.Nexus
{
    internal sealed class NexusPremiumRequiredException : NexusApiException
    {
        public NexusPremiumRequiredException()
            : base("下载功能需要 Nexus Mods Premium 会员资格", 403, "PremiumRequired")
        {
        }

        public NexusPremiumRequiredException(string message)
            : base(message, 403, "PremiumRequired")
        {
        }
    }
}
