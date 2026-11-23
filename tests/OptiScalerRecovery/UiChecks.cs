using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using ClawControl.Core;
using ClawControl.Features.Library;
using ClawControl.Features.OptiScaler;
using ClawControl.Features;
using ClawControl.UI;

internal static class UiChecks
{
    public static Task Run(string root, GameTarget target)
    {
        var completed = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("/ClawControl;component/UI/Theme.xaml",UriKind.Relative) });
                var prefs = new Settings();
                var library = new LibraryService(prefs);
                var svc = new OptiScalerService(prefs,library);
                IPage? opened = null;
                var list = new OptiScalerScreen(svc,()=>null,p=>opened=p,()=>{},()=>{});
                list.OnShown();
                var box=(StackPanel)list.Content;
                var opt = box.Children.OfType<Button>().Single();
                opt.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!svc.Enabled) throw new Exception("Beta button did not enable feature");
                Console.WriteLine("PASS beta opt-in through WPF page");
                var item = new LibraryItem { Name = "Library QA", Source = "Program", Launch = target.Exe!, GameProgram = target.Exe };
                prefs.LibraryGames.Add(item);
                var libraryPage = new LibraryPage(library,p=>opened=p,()=>{},()=>{}) { OptiScaler = list };
                libraryPage.OnShown();
                var card = Buttons(libraryPage).Single(b => PanelInput.GetActionHint(b) == "Options");
                PanelInput.GetAction(card)!();
                if (opened?.Title != item.Name) throw new Exception("X did not open the selected game's options");
                var options = Buttons(opened.View).ToArray();
                options[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (opened is not OptiScalerGameScreen detail || detail.Target.Exe != target.Exe) throw new Exception("Wrong OptiScaler game");
                options[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (opened?.Id != "library-cover") throw new Exception("Change cover did not open");
                libraryPage.OnHidden();
                prefs.LibraryGames.Remove(item);
                Console.WriteLine("PASS Library X > Options opens OptiScaler and Cover for the selected game");
                bool closed=false;
                var game = new OptiScalerGameScreen(svc,target,_=>{},()=>closed=true,()=>{});
                game.OnShown();
                var page=(StackPanel)game.Content;
                Button Primary()=>page.Children.OfType<Button>().First();
                string Label(Button b)=>((TextBlock)b.Content).Text;
                void PumpUntil(Func<bool> done)
                {
                    var until=DateTime.UtcNow.AddSeconds(15);
                    while(!done() && DateTime.UtcNow<until)
                    {
                        Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                        Thread.Sleep(10);
                    }
                    if(!done()) throw new Exception("UI timeout: " + string.Join(" | ",page.Children.OfType<TextBlock>().Select(t=>t.Text)));
                }
                PumpUntil(()=>Primary().IsEnabled);
                Primary().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(!Label(Primary()).Contains("Confirm") || svc.InstallFor(target)!=null) throw new Exception("missing per-game consent");
                Primary().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(()=>Label(Primary())=="Return to desktop");
                Primary().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(!closed || svc.InstallFor(target)==null) throw new Exception("primary removed installation");
                Console.WriteLine("PASS install confirmation and harmless primary action after install");
                var remove=page.Children.OfType<Button>().Single(b=>Label(b)=="Remove OptiScaler");
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(svc.InstallFor(target)==null || !Label(remove).Contains("confirm",StringComparison.OrdinalIgnoreCase)) throw new Exception("remove did not require confirmation");
                remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(()=>svc.InstallFor(target)==null);
                Console.WriteLine("PASS controller page removal confirmation and cleanup");
                completed.SetResult();
            }
            catch(Exception ex) { completed.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task;
    }

    static IEnumerable<Button> Buttons(DependencyObject element)
    {
        if (element is Button button) yield return button;
        else
            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
                foreach (var descendant in Buttons(child)) yield return descendant;
    }
}
