using System.IO;
using DuplicadosApp.Models;
using DuplicadosApp.Services;
using Xunit;

namespace DuplicadosApp.Tests;

public sealed class SafetyAndDeletionTests : IDisposable
{
    private readonly string _root;
    private readonly string _trashRoot;

    public SafetyAndDeletionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"DuplicadosDelTests-{Guid.NewGuid():N}");
        _trashRoot = Path.Combine(_root, "_trash");
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
    public void TrashService_HandlesReadOnlyCollisionsLongPathsAndManifest()
    {
        var trash = new TrashService(_trashRoot, new DateTime(2026, 11, 23, 10, 30, 0));

        var readOnlyFile = Path.Combine(_root, "solo_lectura.txt");
        File.WriteAllText(readOnlyFile, "contenido importante");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        Assert.True(trash.Guardar(readOnlyFile));
        Assert.False(File.Exists(readOnlyFile));

        // Ruta profundamente anidada para forzar el acortamiento seguro (_rutas_largas)
        var deepDir = Path.Combine(
            _root,
            new string('a', 45),
            new string('b', 45),
            new string('c', 45));
        Directory.CreateDirectory(deepDir);
        var deepFile = Path.Combine(deepDir, "archivo_con_nombre_muy_largo_para_probar_el_limite_de_ruta.txt");
        File.WriteAllText(deepFile, "contenido profundo");

        Assert.True(trash.Guardar(deepFile));
        Assert.False(File.Exists(deepFile));

        trash.GuardarManifiesto();

        var manifestPath = Path.Combine(trash.Carpeta, "manifiesto.txt");
        Assert.True(File.Exists(manifestPath));
        var manifest = File.ReadAllText(manifestPath);
        Assert.Contains(readOnlyFile, manifest);
        Assert.Contains(deepFile, manifest);
    }

    [Fact]
    public void FileDeleter_RefusesToDeleteModifiedFile_AndMovesVerifiedDuplicate()
    {
        var data = new byte[4096];
        new Random(123).NextBytes(data);

        var keepPath = Path.Combine(_root, "keep.bin");
        var dupPath = Path.Combine(_root, "dup.bin");
        var modifiedPath = Path.Combine(_root, "modified.bin");

        File.WriteAllBytes(keepPath, data);
        File.WriteAllBytes(dupPath, data);
        File.WriteAllBytes(modifiedPath, data);

        var keepEntry = FileSystemService.TryGetEntry(keepPath)!;
        var dupEntry = FileSystemService.TryGetEntry(dupPath)!;
        var modEntry = FileSystemService.TryGetEntry(modifiedPath)!;

        // Modifica uno de los archivos DESPUÉS del escaneo
        var altered = (byte[])data.Clone();
        altered[100] ^= 0xFF;
        File.WriteAllBytes(modifiedPath, altered);

        var all = new List<DeleteCandidate>
        {
            new(keepEntry.Path, keepEntry.Size, keepEntry.Identity, 0),
            new(dupEntry.Path, dupEntry.Size, dupEntry.Identity, 0),
            new(modEntry.Path, modEntry.Size, modEntry.Identity, 0),
        };
        var selected = new List<DeleteCandidate> { all[1], all[2] };

        var progressReported = new List<double>();
        var deleter = new FileDeleter(
            _ => { },
            () => false,
            () => false,
            papeleraPropia: () => new TrashService(_trashRoot, DateTime.Now),
            progress: p => progressReported.Add(p.Percentage));

        var outcome = deleter.Delete(selected, all);

        Assert.Equal(DeleteDestination.PapeleraDeLaApp, outcome.Destino);
        Assert.Equal(1, outcome.Deleted);
        Assert.Equal(1, outcome.Errors);
        Assert.False(File.Exists(dupPath));
        Assert.True(File.Exists(keepPath));
        Assert.True(File.Exists(modifiedPath));
        Assert.Contains(progressReported, p => p >= 99.9);
    }
}

