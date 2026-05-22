namespace Helldivers2ModManager.Services.Nexus
{
    internal interface INexusCacheService
    {
        Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration);

        void Remove(string key);

        void Clear();
    }
}