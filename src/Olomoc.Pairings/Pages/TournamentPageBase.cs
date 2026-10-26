using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Olomoc.Pairings.Services;
using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Pairings.Pages;

/// <summary>
/// Page of a tournament (public or admin): loaded with the tokens kept on this device, then
/// refreshed every 30 seconds (live pairings and results).
/// </summary>
public abstract class TournamentPageBase : ComponentBase, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    [Inject] protected LocalizationService L { get; set; } = default!;
    [Inject] protected CatalogService Catalog { get; set; } = default!;
    [Inject] protected TournamentApi Api { get; set; } = default!;
    [Inject] protected MyTournaments Mine { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    [Parameter] public string Code { get; set; } = "";

    protected TournamentView? View { get; private set; }
    protected IReadOnlyList<Problem>? LoadProblems { get; private set; }
    protected bool Ready { get; private set; }
    protected MyTournament? Entry => Mine.Get(Code);

    private readonly CancellationTokenSource _stop = new();
    private string? _copied;

    protected override async Task OnInitializedAsync()
    {
        L.Changed += OnLanguageChanged;
        await L.EnsureLoadedAsync();
        await Catalog.EnsureLoadedAsync();
        await Mine.EnsureLoadedAsync();
        await BeforeLoadAsync();
        await RefreshAsync();
        Ready = true;
        _ = PollAsync(_stop.Token);
    }

    /// <summary>Before the first load (e.g. token received in the address).</summary>
    protected virtual Task BeforeLoadAsync() => Task.CompletedTask;

    protected async Task RefreshAsync()
    {
        var result = await Api.GetAsync(Code, Entry?.OrganizerToken, Entry?.PlayerToken);
        if (result.Value is { } view)
        {
            View = view;
            LoadProblems = null;
            // Remembered for "My tournaments" (up-to-date name); written only if it changes.
            if (Entry?.Name != view.Name) await Mine.RememberAsync(view.Code, view.Name);
        }
        else LoadProblems = result.Problems;
    }

    private async Task PollAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                await RefreshAsync();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { /* page left */ }
    }

    protected string PublicLink => $"{Nav.BaseUri}pairings/t/{View?.Code ?? Code}";

    protected async Task CopyAsync(string text)
    {
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
            _copied = text;
        }
        catch (JSException) { _copied = null; }   // clipboard unavailable: the link stays selectable
    }

    protected string CopyLabel(string text) => L.Ui(_copied == text ? "export.copied" : "export.copy");

    protected async Task<bool> ConfirmAsync(string message) => await JS.InvokeAsync<bool>("confirm", message);

    protected static string DateLabel(DateOnly? date) =>
        date?.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    /// <summary>"Swiss rounds · 3 rounds · 2 cabals per player".</summary>
    protected string Settings(TournamentView v) => string.Join(" · ", new[]
    {
        L.Ui(v.Pairing == PairingMode.Swiss ? (v.AvoidSameAlignment ? "pairings.mode.swissAligned" : "pairings.mode.swiss") : "pairings.mode.alignment"),
        L.Ui("pairings.roundCount", v.RoundCount),
        L.Ui(v.CabalsPerPlayer == 1 ? "pairings.cabalsPerPlayer.one" : "pairings.cabalsPerPlayer.many", v.CabalsPerPlayer),
    });

    private void OnLanguageChanged() => InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        L.Changed -= OnLanguageChanged;
        _stop.Cancel();
        _stop.Dispose();
    }
}
