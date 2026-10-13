using System.Text;
using System.Text.Json;
using DeSignMage.Core.Model;
using DeSignMage.Formats.Palettes;
using DeSignMage.TestSupport;

namespace DeSignMage.Formats.Tests.Palettes;

/// <summary>
/// The palette reader. Most tests build their palettes in the test (no files needed); the ones that read real palettes need
/// <c>local-testdata/palettes</c> (the .PAL and .CLR files plus <c>palette-golden.json</c>, which holds what an independent
/// reader gets from every .PAL) and are skipped when it is missing.
/// </summary>
public class PaletteTests
{
    private static string Folder => LocalData.Under("palettes");

    private static JsonElement Golden() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Folder, "palette-golden.json"))).RootElement;

    public static IEnumerable<object[]> Files() =>
        Directory.Exists(Folder) ? Directory.EnumerateFiles(Folder, "*.PAL").Select(f => new object[] { Path.GetFileName(f) }) : [];

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    private static string Pal(string rest) => PaletteFile.PalTag + " " + rest;

    [LocalFact("palettes/palette-golden.json")]
    public void The_golden_file_covers_every_local_pal()
    {
        var files = Directory.EnumerateFiles(Folder, "*.PAL").Select(Path.GetFileName).Order().ToArray();
        Assert.NotEmpty(files);
        Assert.Equal(files.Length, Golden().EnumerateObject().Count());
    }

    [LocalTheory("palettes/palette-golden.json")]
    [MemberData(nameof(Files))]
    public void Reader_matches_the_independent_reader_entry_by_entry(string file)
    {
        var g = Golden().GetProperty(file);
        var pal = PaletteFile.Load(Path.Combine(Folder, file));

        Assert.Equal(PaletteFileKind.Pal, pal.Kind);
        Assert.Equal(g.GetProperty("version").GetDouble(), pal.Version, 6);
        Assert.Equal(g.GetProperty("flag").GetInt32(), pal.Flag);
        var expected = g.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, pal.Entries.Count);

        var colors = pal.ToPalette("x").Colors;
        Assert.Equal(expected.Length, colors.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            var e = expected[i];
            var entry = pal.Entries[i];
            Assert.Equal(e[0].GetInt32(), entry.ModelCode);
            Assert.Equal(e[1].EnumerateArray().Select(v => v.GetDouble()).ToArray(), entry.Values.ToArray());
            Assert.Equal(e[2].GetString(), entry.Name);
            Assert.Equal(e[3].GetString(), entry.Group);

            var c = colors[i];
            var rgb = e[4].EnumerateArray().Select(v => v.GetInt32()).ToArray();
            Assert.Equal(rgb, new[] { (int)c.R, c.G, c.B });
            Assert.Equal(e[2].GetString(), c.Name);
            Assert.Equal(e[3].GetString() is { Length: > 0 } grp ? grp : null, c.Vendor);
            Assert.Equal(entry.ModelCode == 4 ? ColorModel.Cmyk : ColorModel.Rgb, c.Model);
            if (c.Model == ColorModel.Cmyk) Assert.Equal(entry.Values.ToArray(), new[] { c.Cmyk!.Value.C, c.Cmyk.Value.M, c.Cmyk.Value.Y, c.Cmyk.Value.K });
        }
    }

    [LocalTheory("palettes/palette-golden.json")]
    [MemberData(nameof(Files))]
    public void Writing_a_pal_back_gives_the_same_bytes(string file)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Folder, file));
        Assert.Equal(bytes, PaletteFile.Parse(bytes).ToPalBytes());
    }

    [LocalFact("palettes/Designer.PAL")]
    public void Designer_has_names_and_vendors()
    {
        var designer = PaletteFile.Load(Path.Combine(Folder, "Designer.PAL")).ToPalette("Designer");
        Assert.Equal(213, designer.Colors.Count);
        Assert.Equal(new DocColor(0, 0, 0, 255, "Black", "Major Print Colors"), designer.Colors[0]);
        Assert.Equal("Slate Grey BW98", designer.Colors[1].Name);
        Assert.Equal("Major Print Colors Light Grey BW104", designer.Colors[3].DisplayName);
    }

    [LocalFact("palettes/Roland Color System Library.PAL")]
    public void A_cmyk_palette_keeps_its_components_and_truncated_group_names()
    {
        var roland = PaletteFile.Load(Path.Combine(Folder, "Roland Color System Library.PAL")).ToPalette("Roland");
        Assert.Equal(1147, roland.Colors.Count);
        var first = roland.Colors[0];
        Assert.Equal(ColorModel.Cmyk, first.Model);
        Assert.Equal(new Cmyk(0, 30, 0, 0), first.Cmyk);
        Assert.Equal("RVW-PR35D", first.Name);
        Assert.Equal("Roland Color Syste", first.Vendor);
        Assert.Equal("ROLAND COLOUR", roland.Colors[1].Vendor);
        Assert.Equal(255, first.R);
        Assert.Equal(255, first.B);
    }

    [Fact]
    public void Clr_files_parse_names_and_rgb_and_their_padding_is_dropped_from_the_name()
    {
        var clr = PaletteFile.Parse("Black                          0     0     0\r\nGolden Yellow                242   183     5\r\n"u8.ToArray());
        Assert.Equal(PaletteFileKind.Clr, clr.Kind);
        Assert.Equal(2, clr.Entries.Count);
        var colors = clr.ToPalette("t").Colors;
        Assert.Equal(new DocColor(0, 0, 0, 255, "Black"), colors[0]);
        Assert.Equal(new DocColor(242, 183, 5, 255, "Golden Yellow"), colors[1]);
    }

    [LocalFact("palettes/3M.CLR")]
    public void Every_local_clr_parses()
    {
        var files = Directory.EnumerateFiles(Folder, "*.CLR").ToArray();
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var pal = PaletteFile.Load(f);
            Assert.Equal(PaletteFileKind.Clr, pal.Kind);
            Assert.NotEmpty(pal.Entries);
            Assert.All(pal.Entries, e => Assert.Equal(3, e.Values.Count));
        }
    }

    [Fact]
    public void A_pal_with_rgb_and_cmyk_entries_reads_and_writes_back_the_same_bytes()
    {
        string text = Pal("6.110000 2") + "\r\n0\r\n1|  0   0   0|Black|Basics|\r\n4|0.00 50.00 100.00 20.00|Amber|Basics|\r\n";
        var file = PaletteFile.Parse(Bytes(text));
        Assert.Equal(PaletteFileKind.Pal, file.Kind);
        var colors = file.ToPalette("t").Colors;
        Assert.Equal(2, colors.Count);
        Assert.Equal(ColorModel.Rgb, colors[0].Model);
        Assert.Equal(ColorModel.Cmyk, colors[1].Model);
        Assert.Equal(new Cmyk(0, 50, 100, 20), colors[1].Cmyk);
        Assert.Equal("Basics", colors[1].Vendor);
        Assert.Equal(Bytes(text), file.ToPalBytes());
    }

    [Fact]
    public void Bad_files_are_refused_with_a_message()
    {
        Assert.Throws<PaletteFormatException>(() => PaletteFile.Parse(Bytes(Pal("6.110000 2\r\n0\r\n1|  0   0   0|Black|G|\r\n"))));   // count mismatch
        Assert.Throws<PaletteFormatException>(() => PaletteFile.Parse(Bytes(Pal("6.110000 1\r\n0\r\nnonsense\r\n"))));
        Assert.Throws<PaletteFormatException>(() => PaletteFile.Parse("this is not a palette"u8.ToArray()));
        Assert.Throws<PaletteFormatException>(() => PaletteFile.Parse(Bytes(Pal("x y\r\n0\r\n"))));
    }

    [Fact]
    public void Unknown_models_are_kept_in_the_file_but_have_no_color()
    {
        var pal = PaletteFile.Parse(Bytes(Pal("6.110000 2\r\n0\r\n9|1 2 3 4 5|Odd|G|\r\n1|  1   2   3|Fine|G|\r\n")));
        Assert.Equal(2, pal.Entries.Count);
        Assert.Single(pal.ToPalette("t").Colors);
        Assert.Contains("9|", Encoding.ASCII.GetString(pal.ToPalBytes()));
    }

    [Fact]
    public void Library_loads_a_folder_pal_wins_over_clr_and_sorts_by_name()
    {
        var dir = Directory.CreateTempSubdirectory("palettes-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Zeta.PAL"), Bytes(Pal("6.110000 1\r\n0\r\n1|  1   2   3|Fine|G|\r\n")));
            File.WriteAllBytes(Path.Combine(dir, "alpha.PAL"), Bytes(Pal("6.110000 1\r\n0\r\n1|  4   5   6|Fine|G|\r\n")));
            File.WriteAllText(Path.Combine(dir, "alpha.CLR"), "Black                          0     0     0\r\nWhite                        255   255   255\r\n");
            File.WriteAllText(Path.Combine(dir, "Only.CLR"), "Black                          0     0     0\r\n");
            File.WriteAllText(Path.Combine(dir, "broken.PAL"), Pal("oops"));
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a palette");

            var problems = new List<string>();
            var all = PaletteLibrary.LoadFolder(dir, problems);
            Assert.Equal(["alpha", "Only", "Zeta"], all.Select(p => p.Name));
            Assert.Single(problems);
            Assert.StartsWith("broken.PAL", problems[0]);
            Assert.Single(all[0].Colors);   // alpha.PAL (one color) won over alpha.CLR (two)
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [LocalFact("palettes/Designer.PAL")]
    public void Library_loads_the_local_palettes()
    {
        var problems = new List<string>();
        var all = PaletteLibrary.LoadFolder(Folder, problems);
        Assert.Empty(problems);
        Assert.Contains(all, p => p.Name == PaletteLibrary.DefaultPaletteName);
        Assert.Equal(all.Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase), all.Select(p => p.Name));
        Assert.Single(all, p => p.Name.Equals("3M", StringComparison.OrdinalIgnoreCase));   // both 3M.PAL and 3M.CLR exist: the .PAL is used
    }

    [Fact]
    public void Library_of_a_missing_folder_is_empty_and_the_starter_set_exists()
    {
        Assert.Empty(PaletteLibrary.LoadFolder(Path.Combine(AppContext.BaseDirectory, "no-such-folder")));
        Assert.True(PaletteLibrary.Starter().Colors.Count >= 20);
    }
}
