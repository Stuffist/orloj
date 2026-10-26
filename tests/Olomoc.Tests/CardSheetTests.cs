using Olomoc.Components;
using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Card sheet: landscape A4 layout, address of the sheet, card images.</summary>
public class CardSheetTests
{
    [Fact]
    public void Sheet_centered_in_the_page_with_the_intended_gaps()
    {
        var all = Enumerable.Range(0, 3).SelectMany(c => new[] { CardSheet.Slot(c, 0, "x"), CardSheet.Slot(c, 1, "x") }).ToList();
        var right = all.Max(c => c.XMm) + CardSheet.CardWidthMm;
        var bottom = all.Max(c => c.YMm) + CardSheet.CardHeightMm;

        Assert.Equal(CardSheet.LeftMm, CardSheet.PageWidthMm - right, 6);       // centered horizontally
        Assert.Equal(CardSheet.TopMm, CardSheet.PageHeightMm - bottom, 6);      // and vertically
        Assert.True(CardSheet.LeftMm > 5 && CardSheet.TopMm > 5, "marges imprimables");
        Assert.Equal(CardSheet.ColumnGapMm, CardSheet.Slot(1, 0).XMm - CardSheet.Slot(0, 0).XMm - CardSheet.CardWidthMm, 6);
        Assert.Equal(CardSheet.RowGapMm, CardSheet.Slot(0, 1).YMm - CardSheet.Slot(0, 0).YMm - CardSheet.CardHeightMm, 6);
        Assert.True(CardSheet.RowGapMm < CardSheet.ColumnGapMm, "cog and equipment closer together than cogs");
    }

    [Fact]
    public void Style_in_percentages_of_the_page()
    {
        var style = CardSheet.Style(CardSheet.Slot(0, 0));
        Assert.Equal("left:3.704%;top:19.643%;width:29.966%;height:30%", style);
    }

    [Fact]
    public async Task Sheet_address_round_trip()
    {
        var d = await AppData.GetAsync();
        Cog?[] cogs = { d.Cog("3R05"), d.Cog("2R04"), d.Cog("1R01") };
        Equipment?[] equipment = { d.Equipment("E11" /* Harmonium */), null, d.Equipment("E13" /* Asservisseur mental */) };

        var value = CardSheet.Encode(cogs, equipment);
        Assert.Equal($"3R05.E11,2R04,1R01.{equipment[2]!.Id}", value);

        var (readCogs, readEquipment) = CardSheet.Decode(value, d.Catalog);
        Assert.Equal(cogs.Select(c => c?.Id), readCogs.Select(c => c?.Id));
        Assert.Equal(equipment.Select(e => e?.Id), readEquipment.Select(e => e?.Id));

        var cells = CardSheet.Cells(readCogs, readEquipment);
        Assert.Equal(5, cells.Count);                                               // one missing equipment
        // Like the builder: rank 3 on the left, rank 1 on the right; cog on top, equipment below.
        Assert.Equal(("1R01", 0, 0), (cells[0].Id, cells[0].Column, cells[0].Row));
        Assert.Equal((equipment[2]!.Id, 0, 1), (cells[1].Id, cells[1].Column, cells[1].Row));
        Assert.Equal(("2R04", 1, 0), (cells[2].Id, cells[2].Column, cells[2].Row));
        Assert.Equal(("3R05", 2, 0), (cells[3].Id, cells[3].Column, cells[3].Row));
        Assert.Equal(("E11", 2, 1), (cells[4].Id, cells[4].Column, cells[4].Row));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(",,")]
    [InlineData("XXXX.E11,2R04.YYY")]
    public async Task Incomplete_or_unknown_address_tolerated(string? value)
    {
        var d = await AppData.GetAsync();
        var (cogs, equipment) = CardSheet.Decode(value, d.Catalog);
        Assert.Null(cogs[0]);
        Assert.Null(equipment[0]);
        Assert.Null(equipment[1]);
    }

    [Fact]
    public async Task Every_catalog_card_has_its_image()
    {
        var d = await AppData.GetAsync();
        var ids = d.Catalog.Catalog.Cogs.Select(c => c.Id).Concat(d.Catalog.Catalog.Equipment.Select(e => e.Id));
        var missing = ids.Where(id => !File.Exists(Path.Combine(AppData.WwwRoot, "img", "cards", id + ".jpg"))).ToList();
        Assert.Empty(missing);

        // Landscape format 89 × 63 mm: 1051 × 744 px (JPEG header, SOF segment).
        var (width, height) = JpegSize(Path.Combine(AppData.WwwRoot, "img", "cards", "E11.jpg"));
        Assert.Equal((1051, 744), (width, height));
    }

    [Fact]
    public async Task Every_faction_and_every_Ministry_has_its_emblem()
    {
        var d = await AppData.GetAsync();
        var paths = d.Catalog.AllFactions.Select(f => Emblem.FactionPath(f.Code))
            .Concat(d.Catalog.Catalog.Ministries.Select(m => Emblem.MinistryPath(m.Code)))
            .ToList();
        Assert.Equal(16, paths.Count);
        var missing = paths.Where(p => !File.Exists(Path.Combine(AppData.WwwRoot, p[Assets.Base.Length..]))).ToList();
        Assert.Empty(missing);
    }

    private static (int Width, int Height) JpegSize(string path)
    {
        var b = File.ReadAllBytes(path);
        for (var i = 2; i + 9 < b.Length;)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            var length = (b[i + 2] << 8) | b[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            i += 2 + length;
        }
        throw new InvalidDataException(path);
    }
}
