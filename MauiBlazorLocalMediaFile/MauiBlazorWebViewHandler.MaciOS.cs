﻿using Foundation;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.Logging;
using MauiBlazorLocalMediaFile.Utilities;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using UIKit;
using WebKit;
using RectangleF = CoreGraphics.CGRect;

namespace MauiBlazorLocalMediaFile
{
#nullable disable
    public partial class MauiBlazorWebViewHandler
    {
        private BlazorWebViewHandlerReflection _base;

        private BlazorWebViewHandlerReflection Base => _base ??= new(this);

        [SupportedOSPlatform("ios11.0")]
        protected override WKWebView CreatePlatformView()
        {
            Base.LoggerCreatingWebKitWKWebView();

            var config = new WKWebViewConfiguration();

            // By default, setting inline media playback to allowed, including autoplay
            // and picture in picture, since these things MUST be set during the webview
            // creation, and have no effect if set afterwards.
            // A custom handler factory delegate could be set to disable these defaults
            // but if we do not set them here, they cannot be changed once the
            // handler's platform view is created, so erring on the side of wanting this
            // capability by default.
            if (OperatingSystem.IsMacCatalystVersionAtLeast(10) || OperatingSystem.IsIOSVersionAtLeast(10))
            {
                config.AllowsPictureInPictureMediaPlayback = true;
                config.AllowsInlineMediaPlayback = true;
                config.MediaTypesRequiringUserActionForPlayback = WKAudiovisualMediaTypes.None;
            }

            VirtualView.BlazorWebViewInitializing(new BlazorWebViewInitializingEventArgs()
            {
                Configuration = config
            });

            // Legacy Developer Extras setting.
            config.Preferences.SetValueForKey(NSObject.FromObject(Base.DeveloperToolsEnabled), new NSString("developerExtrasEnabled"));

            config.UserContentController.AddScriptMessageHandler(Base.CreateWebViewScriptMessageHandler(), "webwindowinterop");
            config.UserContentController.AddUserScript(new WKUserScript(
                new NSString(Base.BlazorInitScript), WKUserScriptInjectionTime.AtDocumentEnd, true));

            // iOS WKWebView doesn't allow handling 'http'/'https' schemes, so we use the fake 'app' scheme
            config.SetUrlSchemeHandler(new SchemeHandler(this), urlScheme: "app");

            var webview = new WKWebView(RectangleF.Empty, config)
            {
                BackgroundColor = UIColor.Clear,
                AutosizesSubviews = true
            };

            if (OperatingSystem.IsIOSVersionAtLeast(16, 4) || OperatingSystem.IsMacCatalystVersionAtLeast(13, 3))
            {
                // Enable Developer Extras for Catalyst/iOS builds for 16.4+
                webview.SetValueForKey(NSObject.FromObject(Base.DeveloperToolsEnabled), new NSString("inspectable"));
            }

            VirtualView.BlazorWebViewInitialized(Base.CreateBlazorWebViewInitializedEventArgs(webview));

            Base.LoggerCreatedWebKitWKWebView();

            return webview;
        }

        private class SchemeHandler : NSObject, IWKUrlSchemeHandler
        {
            private readonly MauiBlazorWebViewHandler _webViewHandler;

            public SchemeHandler(MauiBlazorWebViewHandler webViewHandler)
            {
                _webViewHandler = webViewHandler;
            }

            [Export("webView:startURLSchemeTask:")]
            [SupportedOSPlatform("ios11.0")]
            public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
            {
                var intercept = InterceptCustomPathRequest(urlSchemeTask);
                if (intercept)
                {
                    return;
                }

                var responseBytes = GetResponseBytes(urlSchemeTask.Request.Url?.AbsoluteString ?? "", out var contentType, statusCode: out var statusCode);
                if (statusCode == 200)
                {
                    using (var dic = new NSMutableDictionary<NSString, NSString>())
                    {
                        dic.Add((NSString)"Content-Length", (NSString)(responseBytes.Length.ToString(CultureInfo.InvariantCulture)));
                        dic.Add((NSString)"Content-Type", (NSString)contentType);
                        // Disable local caching. This will prevent user scripts from executing correctly.
                        dic.Add((NSString)"Cache-Control", (NSString)"no-cache, max-age=0, must-revalidate, no-store");
                        if (urlSchemeTask.Request.Url != null)
                        {
                            using var response = new NSHttpUrlResponse(urlSchemeTask.Request.Url, statusCode, "HTTP/1.1", dic);
                            urlSchemeTask.DidReceiveResponse(response);
                        }

                    }
                    urlSchemeTask.DidReceiveData(NSData.FromArray(responseBytes));
                    urlSchemeTask.DidFinish();
                }
            }

            private byte[] GetResponseBytes(string? url, out string contentType, out int statusCode)
            {
                var allowFallbackOnHostPage = _webViewHandler.Base.IsBaseOfPage(_webViewHandler.Base.AppOriginUri, url);
                url = _webViewHandler.Base.QueryStringHelperRemovePossibleQueryString(url);

                _webViewHandler.Base.LoggerHandlingWebRequest(url);

                if (_webViewHandler.Base.TryGetResponseContentInternal(url, allowFallbackOnHostPage, out statusCode, out var statusMessage, out var content, out var headers))
                {
                    statusCode = 200;
                    using var ms = new MemoryStream();

                    content.CopyTo(ms);
                    content.Dispose();

                    contentType = headers["Content-Type"];

                    _webViewHandler?.Base.LoggerResponseContentBeingSent(url, statusCode);

                    return ms.ToArray();
                }
                else
                {
                    _webViewHandler?.Base.LoggerReponseContentNotFound(url);

                    statusCode = 404;
                    contentType = string.Empty;
                    return Array.Empty<byte>();
                }
            }

            [Export("webView:stopURLSchemeTask:")]
            public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
            {
            }

            private static bool InterceptCustomPathRequest(IWKUrlSchemeTask urlSchemeTask)
            {
                var uri = urlSchemeTask.Request.Url.ToString();
                if (uri == null)
                {
                    return false;
                }

                if (!Intercept(uri, out string path))
                {
                    return false;
                }

                if (!File.Exists(path))
                {
                    return false;
                }

                long length = new FileInfo(path).Length;
                string contentType = StaticContentProvider.GetResponseContentTypeOrDefault(path);
                using (var dic = new NSMutableDictionary<NSString, NSString>())
                {
                    dic.Add((NSString)"Content-Length", (NSString)(length.ToString(CultureInfo.InvariantCulture)));
                    dic.Add((NSString)"Content-Type", (NSString)contentType);
                    // Disable local caching. This will prevent user scripts from executing correctly.
                    dic.Add((NSString)"Cache-Control", (NSString)"no-cache, max-age=0, must-revalidate, no-store");
                    using var response = new NSHttpUrlResponse(urlSchemeTask.Request.Url, 200, "HTTP/1.1", dic);
                    urlSchemeTask.DidReceiveResponse(response);
                }

                urlSchemeTask.DidReceiveData(NSData.FromFile(path));
                urlSchemeTask.DidFinish();
                return true;
            }
        }
    }

    public class BlazorWebViewHandlerReflection
    {
        public BlazorWebViewHandlerReflection(BlazorWebViewHandler blazorWebViewHandler)
        {
            _blazorWebViewHandler = blazorWebViewHandler;
            _logger = new(() =>
            {
                var property = Type.GetProperty("Logger", BindingFlags.NonPublic | BindingFlags.Instance);
                return (ILogger)property?.GetValue(_blazorWebViewHandler);
            });
            _blazorInitScript = new(() =>
            {
                var property = Type.GetField("BlazorInitScript", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                return (string)property?.GetValue(_blazorWebViewHandler);
            });
            _appOriginUri = new(() =>
            {
                var property = Type.GetField("AppOriginUri", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                return (Uri)property?.GetValue(_blazorWebViewHandler);
            });
        }

        private readonly BlazorWebViewHandler _blazorWebViewHandler;

        private static readonly Type Type = typeof(BlazorWebViewHandler);

        private static readonly Assembly Assembly = Type.Assembly;

        private static readonly Type TypeLog = Assembly.GetType("Microsoft.AspNetCore.Components.WebView.Log")!;

        private readonly Lazy<ILogger> _logger;

        private readonly Lazy<string> _blazorInitScript;

        private readonly Lazy<Uri> _appOriginUri;

        private object WebviewManager;

        private MethodInfo MethodTryGetResponseContentInternal;

        private MethodInfo MethodIsBaseOfPage;

        private MethodInfo MethodQueryStringHelperRemovePossibleQueryString;

        public ILogger Logger => _logger.Value;

        public string BlazorInitScript => _blazorInitScript.Value;

        public Uri AppOriginUri => _appOriginUri.Value;

        public bool DeveloperToolsEnabled => GetDeveloperToolsEnabled();

        public void LoggerCreatingWebKitWKWebView()
        {
            var method = TypeLog.GetMethod("CreatingWebKitWKWebView");
            method?.Invoke(null, new object[] { Logger });
        }

        public void LoggerCreatedWebKitWKWebView()
        {
            var method = TypeLog.GetMethod("CreatedWebKitWKWebView");
            method?.Invoke(null, new object[] { Logger });
        }

        public void LoggerHandlingWebRequest(string url)
        {
            var method = TypeLog.GetMethod("HandlingWebRequest");
            method?.Invoke(null, new object[] { Logger, url });
        }

        public void LoggerResponseContentBeingSent(string url, int statusCode)
        {
            var method = TypeLog.GetMethod("ResponseContentBeingSent");
            method?.Invoke(null, new object[] { Logger, url, statusCode });
        }

        public void LoggerReponseContentNotFound(string url)
        {
            var method = TypeLog.GetMethod("ReponseContentNotFound");
            method?.Invoke(null, new object[] { Logger, url });
        }

        private bool GetDeveloperToolsEnabled()
        {
            var PropertyDeveloperTools = Type.GetProperty("DeveloperTools", BindingFlags.NonPublic | BindingFlags.Instance);
            var DeveloperTools = PropertyDeveloperTools.GetValue(_blazorWebViewHandler);

            var type = DeveloperTools.GetType();
            var Enabled = type.GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
            return (bool)Enabled?.GetValue(DeveloperTools);
        }

        public IWKScriptMessageHandler CreateWebViewScriptMessageHandler()
        {
            Type webViewScriptMessageHandlerType = Type.GetNestedType("WebViewScriptMessageHandler", BindingFlags.NonPublic);

            if (webViewScriptMessageHandlerType != null)
            {
                // 获取 MessageReceived 方法信息
                MethodInfo messageReceivedMethod = Type.GetMethod("MessageReceived", BindingFlags.Instance | BindingFlags.NonPublic);

                if (messageReceivedMethod != null)
                {
                    // 创建 WebViewScriptMessageHandler 实例
                    object webViewScriptMessageHandlerInstance = Activator.CreateInstance(webViewScriptMessageHandlerType, new object[] { Delegate.CreateDelegate(typeof(Action<Uri, string>), _blazorWebViewHandler, messageReceivedMethod) });
                    return (IWKScriptMessageHandler)webViewScriptMessageHandlerInstance;
                }
            }

            return null;
        }

        public BlazorWebViewInitializedEventArgs CreateBlazorWebViewInitializedEventArgs(WKWebView wKWebView)
        {
            var blazorWebViewInitializedEventArgs = new BlazorWebViewInitializedEventArgs();
            PropertyInfo property = typeof(BlazorWebViewInitializedEventArgs).GetProperty("WebView", BindingFlags.Public | BindingFlags.Instance);
            property.SetValue(blazorWebViewInitializedEventArgs, wKWebView);
            return blazorWebViewInitializedEventArgs;
        }

        public bool TryGetResponseContentInternal(string uri, bool allowFallbackOnHostPage, out int statusCode, out string statusMessage, out Stream content, out IDictionary<string, string> headers)
        {
            if (MethodTryGetResponseContentInternal == null)
            {
                var Field_webviewManager = Type.GetField("_webviewManager", BindingFlags.NonPublic | BindingFlags.Instance);
                WebviewManager = Field_webviewManager.GetValue(_blazorWebViewHandler);

                MethodTryGetResponseContentInternal = WebviewManager.GetType().GetMethod("TryGetResponseContentInternal", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            // 定义参数
            object[] parameters = new object[] { uri, allowFallbackOnHostPage, 0, null, null, null };

            bool result = (bool)MethodTryGetResponseContentInternal.Invoke(WebviewManager, parameters);

            // 获取返回值和输出参数
            statusCode = (int)parameters[2];
            statusMessage = (string)parameters[3];
            content = (Stream)parameters[4];
            headers = (IDictionary<string, string>)parameters[5];
            return result;
        }

        public bool IsBaseOfPage(Uri baseUri, string? uriString)
        {
            if (MethodIsBaseOfPage == null)
            {
                var type = Assembly.GetType("Microsoft.AspNetCore.Components.WebView.Maui.UriExtensions")!;
                MethodIsBaseOfPage = type.GetMethod("IsBaseOfPage", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            }

            return (bool)MethodIsBaseOfPage.Invoke(null, new object[] { baseUri, uriString });
        }

        public string QueryStringHelperRemovePossibleQueryString(string? url)
        {
            if (MethodQueryStringHelperRemovePossibleQueryString == null)
            {
                var type = Assembly.GetType("Microsoft.AspNetCore.Components.WebView.QueryStringHelper")!;
                MethodQueryStringHelperRemovePossibleQueryString = type.GetMethod("RemovePossibleQueryString", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
            }

            return (string)MethodQueryStringHelperRemovePossibleQueryString.Invoke(null, new object[] { url });
        }
    }
}
