﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using Microphone.Core.Util;
using Microsoft.Extensions.Options;

namespace Microphone.Consul
{
    public enum ConsulNameResolution
    {
        HttpApi,
        EbayFabio,
    }

    public class ConsulOptions
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 8500;
        public ConsulNameResolution NameResolution { get; set; } = ConsulNameResolution.HttpApi;
        public int Heartbeat { get; set; } = 1;
    }
    public class ConsulProvider : IClusterProvider
    {
        private readonly string _consulHost;
        private readonly int _consulPort;
        private readonly ConsulNameResolution _nameResolution;
        private string _serviceId;
        private string _serviceName;
        private Uri _uri;
        private string _version;
        private ILogger _log;
        private int _heartbeat;

        public ConsulProvider(ILoggerFactory loggerFactory, IOptions<ConsulOptions> configuration)
        {
            _log = loggerFactory.CreateLogger("Microphone.ConsulProvider");
            _consulHost = configuration.Value.Host;
            _consulPort = configuration.Value.Port;
            _nameResolution = configuration.Value.NameResolution;
            _heartbeat = configuration.Value.Heartbeat;
        }

        public async Task<ServiceInformation[]> ResolveServicesAsync(string serviceName)
        {
            if (_nameResolution == ConsulNameResolution.EbayFabio)
            {
                return new[] { new ServiceInformation($"http://{_consulHost}", 9999) };
            }

            using (var client = new HttpClient())
            {
                var response = await client.GetAsync(ServiceHealthUrl(serviceName));
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception("Could not find services");
                }

                var body = await response.Content.ReadAsStringAsync();
                var res1 = JArray.Parse(body);
                var res =
                    res1.Select(
                        entry =>
                            new ServiceInformation(
                                entry["Service"]["Address"].Value<string>(),
                                entry["Service"]["Port"].Value<int>())
                        ).ToArray();

                return res;
            }
        }

        public async Task RegisterServiceAsync(string serviceName, string serviceId, string version, Uri uri)
        {
            _serviceName = serviceName;
            _serviceId = serviceId;
            _version = version;
            _uri = uri;
            var port = uri.Port;

            var localIp = DnsUtils.GetLocalIPAddress();
            var check = $"http://{localIp}:{port}/status";

            _log.LogInformation($"Using Consul at {_consulHost}:{_consulPort}");
            _log.LogInformation($"Registering service {serviceId} at http://{localIp}:{port}");
            _log.LogInformation($"Registering health check at http://{localIp}:{port}/status");
            var payload = new
            {
                ID = serviceId,
                Name = serviceName,
                Tags = new[] { $"urlprefix-/{serviceName}" },
                Address = localIp,
                // ReSharper disable once RedundantAnonymousTypePropertyName
                Port = uri.Port,
                Check = new
                {
                    HTTP = check,
                    Interval = $"{_heartbeat}s"
                }
            };

            using (var client = new HttpClient())
            {
                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json);

                var res = await client.PostAsync(RegisterServiceUrl, content);
                if (res.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception("Could not register service");
                }
                _log.LogInformation($"Registration successful");
            }
        }

        public async Task KeyValuePutAsync(string key, string value)
        {
            using (var client = new HttpClient())
            {

                var content = new StringContent(value);

                var response = await client.PutAsync(KeyValueUrl(key), content);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception("Could not put value");
                }
            }
        }

        public async Task<string> KeyValueGetAsync(string key)
        {
            using (var client = new HttpClient())
            {
                var response = await client.GetAsync(KeyValueUrl(key));

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new Exception($"There is no value for key \"{key}\"");
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception($"Could not get value for key \"{key}\"");
                }

                var body = await response.Content.ReadAsStringAsync();
                var deserializedBody = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(body);
                var bytes = Convert.FromBase64String((string)deserializedBody[0]["Value"]);
                var strValue = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
                return strValue;
            }
        }

	    private string RootUrl => $"http://{_consulHost}:{_consulPort}";
        private string CriticalServicesUrl => $"{RootUrl}/v1/health/state/critical";
        private string RegisterServiceUrl => $"{RootUrl}/v1/agent/service/register";
        private string KeyValueUrl(string key) => $"{RootUrl}/v1/kv/{key}";
        private string ServiceHealthUrl(string service) => $"{RootUrl}/v1/health/service/{service}";
        private string DeregisterServiceUrl(string service) => $"{RootUrl}/v1/agent/service/deregister/{service}";
    }
}
