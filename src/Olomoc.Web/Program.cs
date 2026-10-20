using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Olomoc.Pairings.Services;
using Olomoc.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Olomoc.Web.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Communs.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<LocalizationService>();
builder.Services.AddScoped<AccountService>();           // session (cookie)
builder.Services.AddScoped<ICabalCloud>(sp => sp.GetRequiredService<AccountService>());   // cabales du compte
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<CabalExporter>();
builder.Services.AddScoped<CabalLibrary>();             // « Mes cabales »
builder.Services.AddScoped<GameStore>();                // partie en cours (menu, mode partie)

// Constructeur.
builder.Services.AddScoped<CabalDraft>();
builder.Services.AddScoped<Olomoc.Builder.Shared.AdminCatalogApi>();   // éditeur du référentiel (éditeurs et admins)

// Tournois.
builder.Services.AddScoped<CabalImport>();              // cabale collée (texte ou JSON)
builder.Services.AddScoped<TournamentApi>();
builder.Services.AddScoped<MyTournaments>();

await builder.Build().RunAsync();
