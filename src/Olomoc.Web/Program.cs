using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Olomoc.Online.Services;
using Olomoc.Pairings.Services;
using Olomoc.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Olomoc.Web.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Shared.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<LocalizationService>();
builder.Services.AddScoped<AccountService>();           // session (cookie)
builder.Services.AddScoped<ICabalCloud>(sp => sp.GetRequiredService<AccountService>());   // account cabals
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<CabalExporter>();
builder.Services.AddScoped<CabalLibrary>();             // "My cabals"
builder.Services.AddScoped<GameStore>();                // game in progress (menu, play mode)

// Builder.
builder.Services.AddScoped<CabalDraft>();
builder.Services.AddScoped<Olomoc.Builder.Shared.AdminCatalogApi>();   // card catalog editor (editors and admins)
builder.Services.AddScoped<Olomoc.Builder.Shared.AdminApi>();          // admin panel (admins)

// Tournaments.
builder.Services.AddScoped<CabalImport>();              // pasted cabal (text or JSON)
builder.Services.AddScoped<TournamentApi>();
builder.Services.AddScoped<MyTournaments>();

// Online games.
builder.Services.AddScoped<OnlineApi>();
builder.Services.AddScoped<OnlineConnection>();         // SignalR hub, opened on a game's page
builder.Services.AddScoped<MyGames>();
builder.Services.AddScoped<OnlineLauncher>();          // lobby opened, which became the current game

await builder.Build().RunAsync();
