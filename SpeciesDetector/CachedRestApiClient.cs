using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VideoOS.Platform.JsonHandling;
using VideoOS.Platform.Login;
using VideoOS.Platform.Proxy.RestApi;

namespace SpeciesDetector
{
    class CachedRestApiClient : IDisposable
    {
        private readonly RestApiClient _restApiClient = new RestApiClient();
        private readonly SemaphoreSlim _clientSemaphore = new SemaphoreSlim(10);
        private readonly Dictionary<string, Task<JsonObject>> _cache = new Dictionary<string, Task<JsonObject>>();
        private readonly object _cacheLock = new object();

        public CachedRestApiClient(LoginSettings loginSettings)
        {
            _restApiClient.Initialize(loginSettings, false);
        }

        public Task<JsonObject> LookupResourceAsync(string resourcePath)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(resourcePath, out var result))
                    return result;

                result = GetResourceAsync(resourcePath);
                _cache[resourcePath] = result;
                return result;
            }
        }

        private async Task<JsonObject> GetResourceAsync(string resourcePath)
        {
            await _clientSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => _restApiClient.GetResources(resourcePath)).ConfigureAwait(false);
            }
            finally
            {
                _clientSemaphore.Release();
            }
        }

        public void Dispose()
        {
            _restApiClient.Dispose();
        }
    }
}