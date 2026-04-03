using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;

namespace SyncLightBridge.Services;

public class HyperHdrService {
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    public HyperHdrService(SettingsService settingsService) {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(2);
        _settingsService = settingsService;
    }

    public async Task SetSyncStateAsync(bool enable) {
        try {
            string url = _settingsService.CurrentSettings.HyperHdrApiUrl;
            
            var payloadSystemGrabber = new {
                command = "componentstate",
                componentstate = new {
                    component = "SYSTEMGRABBER",
                    state = enable
                }
            };

            var payloadLed = new {
                command = "componentstate",
                componentstate = new {
                    component = "LEDDEVICE",
                    state = enable
                }
            };

            var content1 = new StringContent(JsonSerializer.Serialize(payloadSystemGrabber), Encoding.UTF8, "application/json");
            var content2 = new StringContent(JsonSerializer.Serialize(payloadLed), Encoding.UTF8, "application/json");

            var tasks = new[] {
                _httpClient.PostAsync(url, content1),
                _httpClient.PostAsync(url, content2)
            };

            await Task.WhenAll(tasks);
            Debug.WriteLine($"HyperHDR State Overridden -> SYSTEM/VIDEO GRABBER & LEDDEVICE: {enable}");
        } catch (Exception ex) {
            Debug.WriteLine($"HyperHdr API Error: {ex.Message}");
        }
    }
}
