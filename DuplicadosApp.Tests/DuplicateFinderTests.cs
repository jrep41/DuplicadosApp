using System.IO;
using System.Runtime.InteropServices;
using DuplicadosApp.Models;
using DuplicadosApp.Services;
using Xunit;

namespace DuplicadosApp.Tests;

public sealed class DuplicateFinderTests : IDisposable
{
    private readonly string _root;

    public DuplicateFinderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"DuplicadosAppTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void FindsSmallMediumAndLargeDuplicates_AndDistinguishesMiddleDifference()
    {
        // 1) Pequeños (<= 16 KiB)
        var smallData = CreatePattern(4096, seed: 1);
        WriteFile("sub1/small_a.txt", smallData);
        WriteFile("sub2/small_b.txt", smallData);
        var smallDiff = (byte[])smallData.Clone();
        smallDiff[2000] ^= 0xFF;
        WriteFile("sub2/small_diff.txt", smallDiff);

        // 2) Grandes (>= 128 KiB): misma cabecera (8 KiB) y misma cola (8 KiB), pero diferencia en medio
        var largeData = CreatePattern(128 * 1024, seed: 7);
        WriteFile("large/a.bin", largeData);
        WriteFile("large/b.bin", largeData);
        WriteFile("large/c.bin", largeData);

        var sameHeadTailDiffMid = (byte[])largeData.Clone();
        // Cambiamos un byte fuera de cabecera (0..8192), fuera del centro (63488..67584) y fuera de la cola
        sameHeadTailDiffMid[20_000] ^= 0xAA;
        WriteFile("large/diff_mid.bin", sameHeadTailDiffMid);

        var sameHeadDiffTail = (byte[])largeData.Clone();
        sameHeadDiffTail[^10] ^= 0x55;
        WriteFile("large/diff_tail.bin", sameHeadDiffTail);

        var progressValues = new List<double>();
        var finder = new DuplicateFinder(
            _ => { },
            p => progressValues.Add(p.Percentage),
            () => false,
            () => false);

        var result = finder.Run(_root);

        Assert.False(result.Cancelled);
        Assert.Equal(0, result.Errors);
        Assert.Equal(2, result.Groups.Count);

        // Ordenados por espacio recuperable descendente: primero large (2 * 128 KiB), luego small (1 * 4 KiB)
        Assert.Equal(128 * 1024, result.Groups[0].Size);
        Assert.Equal(3, result.Groups[0].Files.Count);
        Assert.DoesNotContain(result.Groups[0].Files, f => f.Path.EndsWith("diff_mid.bin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Groups[0].Files, f => f.Path.EndsWith("diff_tail.bin", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(4096, result.Groups[1].Size);
        Assert.Equal(2, result.Groups[1].Files.Count);

        // El progreso llega hasta 100 %
        Assert.Contains(progressValues, v => v >= 99.9);
    }

    [Fact]
    public void RespectsScanOptions_EmptyFilesAndExtensionRequirement()
    {
        WriteFile("empty1.txt", []);
        WriteFile("empty2.txt", []);

        var data = CreatePattern(2048, seed: 42);
        WriteFile("photo.jpg", data);
        WriteFile("photo_copy.jpeg", data);

        // Con opciones por defecto (Ignorar vacíos = true, Misma extensión = true): 0 duplicados
        var defaultResult = new DuplicateFinder(_ => { }, _ => { }, () => false, () => false, options: ScanOptions.Default)
            .Run(_root);
        Assert.Empty(defaultResult.Groups);

        // Permitiendo distinta extensión e incluyendo vacíos: 2 grupos
        var relaxedOptions = new ScanOptions
        {
            MinFileSizeBytes = 0,
            RequireSameExtension = false,
        };
        var relaxedResult = new DuplicateFinder(_ => { }, _ => { }, () => false, () => false, options: relaxedOptions)
            .Run(_root);

        Assert.Equal(2, relaxedResult.Groups.Count);
        Assert.Contains(relaxedResult.Groups, g => g.Size == 2048 && g.Files.Count == 2);
        Assert.Contains(relaxedResult.Groups, g => g.Size == 0 && g.Files.Count == 2);
    }

    [Fact]
    public void DeduplicatesHardLinks_DoesNotReportSamePhysicalFileAsDuplicate()
    {
        var original = WriteFile("original.dat", CreatePattern(32 * 1024, seed: 9));
        var hardLink = Path.Combine(_root, "hardlink.dat");

        if (!CreateHardLinkW(hardLink, original, IntPtr.Zero))
        {
            // En sistemas de archivos sin soporte de hard links (p. ej. FAT32) se omite la aserción.
            return;
        }

        var finder = new DuplicateFinder(_ => { }, _ => { }, () => false, () => false);
        var result = finder.Run(_root);

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.SkippedHardLinks);
    }

    [Fact]
    public async Task PauseGate_WakesImmediatelyOnCancel()
    {
        using var gate = new PauseGate();
        using var cts = new CancellationTokenSource();
        gate.Pause();

        var task = Task.Run(() => gate.WaitIfPaused(cts.Token));
        await Task.Delay(30);
        Assert.False(task.IsCompleted);

        cts.Cancel();
        await task.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Cancellation_StopsImmediately()
    {
        var data = CreatePattern(32 * 1024, seed: 3);
        for (var i = 0; i < 10; i++)
        {
            WriteFile($"dir{i}/f.bin", data);
        }

        var result = new DuplicateFinder(_ => { }, _ => { }, () => false, () => true).Run(_root);
        Assert.True(result.Cancelled);
        Assert.Empty(result.Groups);
    }

    private string WriteFile(string relativePath, byte[] bytes)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
        return fullPath;
    }

    private static byte[] CreatePattern(int length, int seed)
    {
        var rng = new Random(seed);
        var bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);
}
