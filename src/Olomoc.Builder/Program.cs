using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Olomoc.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Olomoc.Builder.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<LocalizationService>();
builder.Services.AddScoped<AccountService>();           // session commune aux apps (cookie)
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<CabalExporter>();
builder.Services.AddScoped<CabalDraft>();
builder.Services.AddScoped<GameStore>();
builder.Services.AddScoped<CabalLibrary>();

await builder.Build().RunAsync();
