using System.Collections.Specialized;
using System.Windows.Threading;
using DuplicadosApp.Models;
using DuplicadosApp.ViewModels;
using Xunit;

namespace DuplicadosApp.Tests;

public sealed class ViewModelTests
{
    [Fact]
    public void DuplicateGroupViewModel_SelectByStrategy_PicksExpectedKeptCopy()
    {
        var group = new DuplicateGroup
        {
            Size = 1024,
            DisplayName = "doc.pdf",
            Files =
            [
                new(@"C:\carpeta\subcarpeta\profunda\doc_medio.pdf", 1024, new(1, 1), new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
                new(@"C:\doc_nuevo.pdf", 1024, new(1, 2), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                new(@"C:\archivo\doc_antiguo.pdf", 1024, new(1, 3), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ],
        };

        var vm = new DuplicateGroupViewModel(group, 0);

        vm.SelectByStrategy(SelectionStrategy.KeepOldest);
        Assert.False(vm.Files[2].IsSelected);
        Assert.True(vm.Files[0].IsSelected);
        Assert.True(vm.Files[1].IsSelected);

        vm.SelectByStrategy(SelectionStrategy.KeepNewest);
        Assert.False(vm.Files[1].IsSelected);
        Assert.True(vm.Files[0].IsSelected);
        Assert.True(vm.Files[2].IsSelected);

        vm.SelectByStrategy(SelectionStrategy.KeepShortestPath);
        Assert.False(vm.Files[1].IsSelected); // C:\doc_nuevo.pdf es la ruta más corta
        Assert.True(vm.Files[0].IsSelected);
        Assert.True(vm.Files[2].IsSelected);

        vm.KeepOnly(vm.Files[0]);
        Assert.False(vm.Files[0].IsSelected);
        Assert.True(vm.Files[1].IsSelected);
        Assert.True(vm.Files[2].IsSelected);

        Assert.True(vm.MatchesFilter("doc.pdf"));
        Assert.True(vm.MatchesFilter("profunda"));
        Assert.False(vm.MatchesFilter("inexistente.zip"));
    }

    [Fact]
    public void BulkObservableCollection_ReplaceAll_EmitsSingleReset()
    {
        var col = new BulkObservableCollection<int>();
        var events = 0;
        NotifyCollectionChangedAction? lastAction = null;
        col.CollectionChanged += (_, e) =>
        {
            events++;
            lastAction = e.Action;
        };

        col.ReplaceAll(Enumerable.Range(0, 1000));

        Assert.Equal(1000, col.Count);
        Assert.Equal(1, events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, lastAction);
    }

    [Fact]
    public void MainViewModel_QuitarDeLosResultados_DropsSingletonGroupsAndUpdatesTotals()
    {
        Exception? threadError = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var vm = new MainViewModel(Dispatcher.CurrentDispatcher);
                var group = new DuplicateGroup
                {
                    Size = 2048,
                    DisplayName = "a.png",
                    Files =
                    [
                        new(@"C:\a1.png", 2048, new(1, 1)),
                        new(@"C:\a2.png", 2048, new(1, 2)),
                    ],
                };

                vm.Groups.ReplaceAll([new DuplicateGroupViewModel(group, 0)]);
                vm.SelectByStrategy(SelectionStrategy.KeepFirst);
                Assert.Equal(1, vm.SelectedCount);
                Assert.Equal(2048, vm.SelectedBytes);

                // Al eliminar una de las 2 copias, queda solo 1 archivo: el grupo deja de ser duplicado.
                vm.QuitarDeLosResultados([@"C:\a2.png"]);

                Assert.Empty(vm.Groups);
                Assert.False(vm.HasResults);
                Assert.Equal(0, vm.SelectedCount);
                Assert.Equal(0, vm.SelectedBytes);
            }
            catch (Exception ex)
            {
                threadError = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(threadError);
    }
}
