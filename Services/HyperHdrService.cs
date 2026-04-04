using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace HdrBridge.Services;

public class HyperHdrService {
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    public HyperHdrService(SettingsService settingsService) {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(2);
        _settingsService = settingsService;
    }

    /// <summary>
    /// Returns true if the HyperHDR JSON-RPC endpoint responds (serverinfo).
    /// </summary>
    public async Task<bool> IsServerAvailableAsync() {
        try {
            string url = _settingsService.CurrentSettings.HyperHdrApiUrl;
            var payload = new { command = "serverinfo", tan = 1 };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(url, content).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        } catch (Exception ex) {
            Debug.WriteLine($"HyperHdr availability check: {ex.Message}");
            return false;
        }
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

            await Task.WhenAll(tasks).ConfigureAwait(false);
            Debug.WriteLine($"HyperHDR State Overridden -> SYSTEM/VIDEO GRABBER & LEDDEVICE: {enable}");
        } catch (Exception ex) {
            Debug.WriteLine($"HyperHdr API Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the global brightness gain on the HyperHDR server (0-100).
    /// </summary>
    public async Task SetBrightnessAsync(int brightnessPercentage) {
        try {
            string url = _settingsService.CurrentSettings.HyperHdrApiUrl;
            var payload = new {
                command = "adjustment",
                adjustment = new { classic_config = true, luminanceGain = Math.Clamp(brightnessPercentage, 0, 100) / 100f }
            };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(url, content).ConfigureAwait(false);
            Debug.WriteLine($"HyperHDR brightness set to {brightnessPercentage}%");
        } catch (Exception ex) {
            Debug.WriteLine($"HyperHdr brightness error: {ex.Message}");
        }
    }
}
