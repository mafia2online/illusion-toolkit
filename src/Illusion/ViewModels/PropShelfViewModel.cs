using System.ComponentModel;

namespace Illusion.ViewModels;

/// <summary>One row of the props browser's shelf list: a category of <see cref="Assets.Library.PropCatalog"/>
/// (or "All"), and how many props on it the search lets through — so a search shows which shelves its hits are
/// on before one is picked.</summary>
public sealed class PropShelfViewModel(string name) : INotifyPropertyChanged
{
    public string Name { get; } = name;

    private int _count;

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
