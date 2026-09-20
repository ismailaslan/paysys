using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Paysys.Web;
using Paysys.Web.Auth;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? builder.HostEnvironment.BaseAddress;

builder.Services.AddSingleton<AuthState>();
builder.Services.AddScoped(sp => new HttpClient(new AuthTokenHandler(sp.GetRequiredService<AuthState>())
{
    InnerHandler = new HttpClientHandler()
})
{
    BaseAddress = new Uri(apiBaseUrl)
});

await builder.Build().RunAsync();
