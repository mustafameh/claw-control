using System.Windows;
using ClawControl.UI;

namespace ClawControl.Features.Library;

internal sealed class GameOptions : IPage
{
    public string Id => "library-game-options";
    public string Title { get; }
    public FrameworkElement View { get; }

    public GameOptions(string gameName, Action openScaler, Action openCover)
    {
        Title = gameName;
        var scaler = new ListRow(null, null, "OptiScaler");
        scaler.Button.Click += (_, _) => openScaler();
        var cover = new ListRow(null, null, "Change cover");
        cover.Button.Click += (_, _) => openCover();
        View = ListRow.Card(scaler, cover);
    }

    public void OnShown() { }
}
