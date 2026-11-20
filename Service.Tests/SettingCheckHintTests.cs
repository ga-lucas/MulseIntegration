using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Hint = Mulse.Ui.Components.Shared.SettingCheckHint;
using Input = Mulse.Ui.Components.Shared.ReferenceInput;

namespace Service.Tests;

public sealed class SettingCheckHintTests
{
    private const string Secret = "s3cr3t-Value!";

    [Fact]
    public async Task ACleanSettingShowsNothingExtra()
    {
        await using var host = new Host("{{secret:sftp-password}}");

        Assert.DoesNotContain("setting-check", await host.RenderAsync());
    }

    [Fact]
    public async Task ATypedInCredentialGetsAQuietNoteAndNeverEchoesTheValueInTheHint()
    {
        await using var host = new Host(Secret);
        var html = await host.RenderAsync();

        Assert.Contains("data-testid=\"setting-check\"", html);
        Assert.Contains("credential typed in as plain text", html);
        Assert.Contains("setting-check-move", html);
        Assert.DoesNotContain("role=\"alert\"", html);
        Assert.Equal(0, host.Handler.Requests);
    }

    [Fact]
    public async Task MovingStoresTheValueAndReplacesItWithASecretReference()
    {
        await using var host = new Host(Secret, stored: ["api-key"]);
        await host.RenderAsync();

        await host.CallAsync("StartMove");
        var form = await host.RenderAsync();
        Assert.Contains("value=\"sftp-password\"", form);

        await host.CallAsync("StoreAsync");

        Assert.Equal("{{secret:sftp-password}}", host.NewValue);
        var put = Assert.Single(host.Handler.Puts);
        Assert.Equal("/api/config-values/sftp-password", put.Path);
        Assert.Equal(Secret, put.Value);
        Assert.DoesNotContain(Secret, await host.RenderAsync());
    }

    [Fact]
    public async Task AnExistingStoredNameIsNeverOverwrittenEvenWhenTheListWasStale()
    {
        await using var host = new Host(Secret, stored: ["sftp-password"], references: []);
        await host.RenderAsync();

        await host.CallAsync("StartMove");
        await host.CallAsync("StoreAsync");
        var html = await host.RenderAsync();

        Assert.Empty(host.Handler.Puts);
        Assert.Null(host.NewValue);
        Assert.Contains("already exists", html);
    }

    [Fact]
    public async Task AFailedStoreLeavesTheSettingUnchangedAndSaysSoWithoutDetails()
    {
        await using var host = new Host(Secret, failPut: true);
        await host.RenderAsync();

        await host.CallAsync("StartMove");
        await host.CallAsync("StoreAsync");
        var html = await host.RenderAsync();

        Assert.Null(host.NewValue);
        Assert.Contains("setting is unchanged", html);
        Assert.DoesNotContain(Secret, html);
        Assert.DoesNotContain("boom", html);
    }

    [Fact]
    public async Task ABadNameIsRefusedBeforeAnythingIsSent()
    {
        await using var host = new Host(Secret);
        await host.RenderAsync();

        await host.CallAsync("StartMove");
        host.SetField("_name", "bad name");
        await host.CallAsync("StoreAsync");

        Assert.Empty(host.Handler.Puts);
        Assert.Contains("Use letters", await host.RenderAsync());
    }

    [Fact]
    public async Task WithoutAnApiClientThePageStillExplainsWhereToAddIt()
    {
        await using var host = new Host(Secret, withApi: false);
        var html = await host.RenderAsync();

        Assert.DoesNotContain("setting-check-move", html);
        Assert.Contains("/config-values", html);
    }

    [Fact]
    public async Task APlaceholderIsNotedWithoutOfferingAMove()
    {
        await using var host = new Host("TODO", key: "host");
        var html = await host.RenderAsync();

        Assert.Contains("never filled in", html);
        Assert.DoesNotContain("setting-check-move", html);
    }

    [Fact]
    public async Task ReferenceInputShowsTheNoteOnlyWhenItKnowsTheSettingIsACredential()
    {
        var withKey = await RenderInputAsync("sftpPassword", "Text", Secret);
        var withoutKey = await RenderInputAsync(null, "Text", Secret);
        var passwordKind = await RenderInputAsync(null, "Password", Secret);
        var choice = await RenderInputAsync("password", "Boolean", "true");

        Assert.Contains("setting-check", withKey);
        Assert.DoesNotContain("setting-check", withoutKey);
        Assert.Contains("setting-check", passwordKind);
        Assert.DoesNotContain("setting-check", choice);
    }

    private static async Task<string> RenderInputAsync(string? key, string kind, string value)
    {
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new StaticRenderJsRuntime()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<Input>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["SettingKey"] = key, ["InputKind"] = kind, ["Value"] = value, ["Label"] = "Test setting"
            }));
            return rendered.ToHtmlString();
        });
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;
        private readonly Activator _activator = new();
        private readonly string _key;
        private readonly string _value;
        private readonly string[] _references;
        private HtmlRootComponent? _root;

        public Host(string value, string key = "sftpPassword", string[]? stored = null, string[]? references = null, bool failPut = false, bool withApi = true)
        {
            _value = value;
            _key = key;
            _references = references ?? stored ?? [];
            Handler = new Handler { Stored = stored ?? [], FailPut = failPut };
            var http = new HttpClient(Handler) { BaseAddress = new("http://localhost") };
            var collection = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(_activator)
                .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new StaticRenderJsRuntime());
            if (withApi) collection.AddSingleton<IMulseApiClient>(new MulseApiClient(http));
            _services = collection.BuildServiceProvider();
            _renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public Handler Handler { get; }

        public string? NewValue { get; private set; }

        public async Task<string> RenderAsync()
            => await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                _root ??= await _renderer.RenderComponentAsync<Hint>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["SettingKey"] = _key, ["Value"] = _value, ["InputKind"] = "Text", ["References"] = _references,
                    ["ValueChanged"] = EventCallback.Factory.Create<string>(this, value => NewValue = value)
                }));
                await Task.Yield();
                return _root.Value.ToHtmlString();
            });

        public Task CallAsync(string method)
            => _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var hint = Assert.Single(_activator.Components.OfType<Hint>());
                var action = typeof(Hint).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!;
                await new EventCallback(hint, (Func<Task>)(() => action.Invoke(hint, null) as Task ?? Task.CompletedTask)).InvokeAsync();
                await Task.Yield();
            });

        public void SetField(string name, object value)
            => typeof(Hint).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Assert.Single(_activator.Components.OfType<Hint>()), value);

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class Activator : IComponentActivator
    {
        public List<IComponent> Components { get; } = [];

        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            Components.Add(component);
            return component;
        }
    }

    private sealed class StaticRenderJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new NotSupportedException("Static rendering must not invoke JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new NotSupportedException("Static rendering must not invoke JavaScript.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string[] Stored { get; init; } = [];

        public bool FailPut { get; init; }

        public int Requests { get; private set; }

        public List<(string Path, string Value)> Puts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/config-values")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Stored.Select(name => new ConfigValueViewModel(name, "x")).ToArray(), options: new(JsonSerializerDefaults.Web)) };
            if (request.Method == HttpMethod.Put && path.StartsWith("/api/config-values/", StringComparison.Ordinal))
            {
                if (FailPut) return new(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                var value = body.GetProperty("value").GetString() ?? "";
                Puts.Add((path, value));
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ConfigValueViewModel(Uri.UnescapeDataString(path["/api/config-values/".Length..]), value), options: new(JsonSerializerDefaults.Web)) };
            }

            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) };
        }
    }
}
