using Olomoc.Components;
using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Card sheet: address of the sheet, card images.</summary>
public class CardSheetTests
{
    [Fact]
    public async Task Sheet_address_round_trip()
    {
        var d = await AppData.GetAsync();
        Cog?[] cogs = { d.Cog("3R05"), d.Cog("2R04"), d.Cog("1R01") };
        Equipment?[] equipment = { d.Equipment("E11" /* Harmonium */), null, d.Equipment("E13" /* Asservisseur mental */) };

        var value = CardSheet.Encode(cogs, equipment);
        Assert.Equal("3R05.E11,2R04,1R01.E13", value);

        var (readCogs, readEquipment) = CardSheet.Decode(value, d.Catalog);
        Assert.Equal(cogs.Select(c => c?.Id), readCogs.Select(c => c?.Id));
        Assert.Equal(equipment.Select(e => e?.Id), readEquipment.Select(e => e?.Id));
        Assert.Null(CardSheet.Decode("XXXX.E11,2R04.YYY", d.Catalog).Cogs[0]);       // unknown: tolerated
    }

    [Fact]
    public async Task Every_card_faction_and_Ministry_has_its_image()
    {
        var d = await AppData.GetAsync();
        var cards = d.Catalog.Catalog.Cogs.Select(c => c.Id).Concat(d.Catalog.Catalog.Equipment.Select(e => e.Id))
            .Select(id => Path.Combine(AppData.WwwRoot, "img", "cards", id + ".jpg"));
        var emblems = d.Catalog.AllFactions.Select(f => Emblem.FactionPath(f.Code))
            .Concat(d.Catalog.Catalog.Ministries.Select(m => Emblem.MinistryPath(m.Code)))
            .Select(p => Path.Combine(AppData.WwwRoot, p[Assets.Base.Length..]));
        Assert.DoesNotContain(cards.Concat(emblems), p => !File.Exists(p));
    }
}
