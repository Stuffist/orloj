using Olomoc.Models;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>Planche des cartes : mise en page A4 paysage, adresse de la planche, images des cartes.</summary>
public class CardSheetTests
{
    [Fact]
    public void Planche_centree_dans_la_page_avec_les_ecarts_voulus()
    {
        var all = Enumerable.Range(0, 3).SelectMany(c => new[] { CardSheet.Slot(c, 0, "x"), CardSheet.Slot(c, 1, "x") }).ToList();
        var right = all.Max(c => c.XMm) + CardSheet.CardWidthMm;
        var bottom = all.Max(c => c.YMm) + CardSheet.CardHeightMm;

        Assert.Equal(CardSheet.LeftMm, CardSheet.PageWidthMm - right, 6);       // centrée horizontalement
        Assert.Equal(CardSheet.TopMm, CardSheet.PageHeightMm - bottom, 6);      // et verticalement
        Assert.True(CardSheet.LeftMm > 5 && CardSheet.TopMm > 5, "marges imprimables");
        Assert.Equal(CardSheet.ColumnGapMm, CardSheet.Slot(1, 0).XMm - CardSheet.Slot(0, 0).XMm - CardSheet.CardWidthMm, 6);
        Assert.Equal(CardSheet.RowGapMm, CardSheet.Slot(0, 1).YMm - CardSheet.Slot(0, 0).YMm - CardSheet.CardHeightMm, 6);
        Assert.True(CardSheet.RowGapMm < CardSheet.ColumnGapMm, "rouage et équipement plus rapprochés qu'entre rouages");
    }

    [Fact]
    public void Style_en_pourcentages_de_la_page()
    {
        var style = CardSheet.Style(CardSheet.Slot(0, 0));
        Assert.Equal("left:3.704%;top:19.643%;width:29.966%;height:30%", style);
    }

    [Fact]
    public async Task Adresse_de_la_planche_aller_retour()
    {
        var d = await AppData.GetAsync();
        Cog?[] cogs = { d.Cog("3R05"), d.Cog("2R04"), d.Cog("1R01") };
        Equipment?[] equipment = { d.Equipment("Harmonium"), null, d.Equipment("Asservisseur mental") };

        var value = CardSheet.Encode(cogs, equipment);
        Assert.Equal($"3R05.E11,2R04,1R01.{equipment[2]!.Id}", value);

        var (readCogs, readEquipment) = CardSheet.Decode(value, d.Catalog);
        Assert.Equal(cogs.Select(c => c?.Id), readCogs.Select(c => c?.Id));
        Assert.Equal(equipment.Select(e => e?.Id), readEquipment.Select(e => e?.Id));

        var cells = CardSheet.Cells(readCogs, readEquipment);
        Assert.Equal(5, cells.Count);                                               // un équipement absent
        Assert.Equal(("3R05", 0, 0), (cells[0].Id, cells[0].Column, cells[0].Row)); // rang 1 à gauche, en haut
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(",,")]
    [InlineData("XXXX.E11,2R04.YYY")]
    public async Task Adresse_incomplete_ou_inconnue_toleree(string? value)
    {
        var d = await AppData.GetAsync();
        var (cogs, equipment) = CardSheet.Decode(value, d.Catalog);
        Assert.Null(cogs[0]);
        Assert.Null(equipment[0]);
        Assert.Null(equipment[1]);
    }

    [Fact]
    public async Task Chaque_carte_du_referentiel_a_son_image()
    {
        var d = await AppData.GetAsync();
        var ids = d.Catalog.Catalog.Cogs.Select(c => c.Id).Concat(d.Catalog.Catalog.Equipment.Select(e => e.Id));
        var missing = ids.Where(id => !File.Exists(Path.Combine(AppData.WwwRoot, "img", "cards", id + ".jpg"))).ToList();
        Assert.Empty(missing);

        // Format paysage 89 × 63 mm : 1051 × 744 px (en-tête JPEG, segment SOF).
        var (width, height) = JpegSize(Path.Combine(AppData.WwwRoot, "img", "cards", "E11.jpg"));
        Assert.Equal((1051, 744), (width, height));
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
