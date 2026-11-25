using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using MPWS.Models;
using MPWS.Services;
using MPWS.ViewModels;

namespace MPWS.Views;
public partial class MainWindow : Window
{
    sealed record Tile(string Title,string Badge,string Detail,string Icon, string Keywords="", bool Highlight=false);
    const string CpuIcon="M4,4 H20 V20 H4 Z M8,8 H16 V16 H8 Z M8,0 V4 M16,0 V4 M8,20 V24 M16,20 V24";
    const string MemoryIcon="M1,5 H23 V18 H1 Z M5,9 H10 V14 H5 Z M14,9 H19 V14 H14 Z M5,18 V22 M10,18 V22 M15,18 V22 M20,18 V22";
    const string NetworkIcon="M9,1 H15 V7 H9 Z M12,7 V13 M4,17 V13 H20 V17 M1,17 H7 V23 H1 Z M17,17 H23 V23 H17 Z";
    const string ClockIcon="M12,2 A10,10 0 1 1 11.99,2 M12,5 V12 L17,15";
    const string ScreenIcon="M1,3 H23 V18 H1 Z M8,23 H16 M12,18 V23 M4,12 H8 L11,7 14,15 17,10 H20";
    const string ServicesIcon="M2,5 H22 M2,12 H22 M2,19 H22 M7,2 V8 M17,9 V15 M9,16 V22";
    readonly Workspace work=new();
    readonly Tile[] areas;
    string category="Все";
    bool scanning;
    public MainWindow()
    {
        InitializeComponent();
        areas=new Tile[]{
            new("Загрузочные таймеры","BCD","Принудительные таймеры и ограничения загрузочной записи.",ClockIcon,"микрофризы фризы тики"),
            new("Питание процессора","CPU","Предел частоты и режим буста от сети и батареи.",CpuIcon),
            new("Память и подкачка","RAM","Выделенная память, доступный предел и размер подкачки.",MemoryIcon,"не хватает памяти out of memory 0xc0000017"),
            new("Графика и захват","GPU","Тайм-ауты видеодрайвера, пакет Game Bar и запись GameDVR.",ScreenIcon,"вылетает драйвер зависает экран черный экран 4101 0x116 video_tdr_failure tdr"),
            new("Системные службы","OS","Запуск и состояние SysMain, Xbox, Defender и служб обновления.",ServicesIcon),
            new("Сетевые интерфейсы","DNS","Адреса DNS-серверов подключённых адаптеров, включая VPN.",NetworkIcon,"сайты долго открываются"),
            new("Планировщик задач","TASK","Фоновые задания в момент проверки.",ClockIcon,"периодические фризы фоновые процессы"),
            new("Реестр и приоритеты","IFEO","Постоянные правила запуска игр и Discord.",CpuIcon,"щелкает звук трещит звук дергается мышь высокий приоритет realtime"),
            new("Сетевой стек","TCP","Автонастройка окна TCP и режим ECN.",NetworkIcon,"медленно скачивает autotuning ecn"),
            new("Питание устройств","USB","Приостановка USB в активной схеме питания.",ServicesIcon,"отваливается usb дергается мышь щелкает звук после простоя")
        };
        DiagnosticTiles.ItemsSource=areas;
        Elevate.Visibility=DiagnosticsService.IsAdmin?Visibility.Collapsed:Visibility.Visible;
        foreach(var name in new[]{"Все","CS2","Dota 2","Discord","Steam"})
        {
            var b=Button(name,()=>{category=name; RenderArticles();});
            b.ContentTemplate=R<DataTemplate>("CategoryLabel");
            Filters.Children.Add(b);
        }
        Query.ToolTip="Введите симптом, название программы или тег";
        Query.TextChanged+=(_,_)=>QueryHint();
        Select(ScanTab); RenderArticles(); RefreshReport(); FillBaseline();
        Loaded+=async (_,_)=>{ if(Environment.GetCommandLineArgs().Contains("--scan")) await Scan(); };
    }
    void FindArea(object sender,TextChangedEventArgs e)
    {
        if(areas==null)return;
        static string Normalize(string s)=>s.ToLowerInvariant().Replace('ё','е');
        var terms=Normalize(Symptom.Text).Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var matches=areas.Where(a=>terms.All(t=>Normalize(a.Title+" "+a.Badge+" "+a.Detail+" "+a.Keywords).Contains(t)))
            .Select(a=>a with { Highlight=terms.Length>0 }).ToList();
        DiagnosticTiles.ItemsSource=matches;
        MatchHint.Text=terms.Length==0?"":matches.Count==0?"Совпадений нет. Попробуйте другое слово.":$"Подходящих областей: {matches.Count}. Совпадение указывает направление проверки.";
    }
    void ToggleBaseline(object sender,RoutedEventArgs e)
    {
        BaselineRows.Visibility=BaselineRows.IsVisible?Visibility.Collapsed:Visibility.Visible;
    }
    void FillBaseline()
    {
        BaselineRows.Children.Clear();
        foreach(var (key,reference,reason) in new[]{
            ("TdrLevel","3","Windows пытается восстановить графику после зависания драйвера."),
            ("TdrDelay","2 с","Штатный тайм-аут позволяет вовремя обнаружить зависание GPU."),
            ("TdrDdiDelay","5 с","Ограничивает ожидание завершения работы потоков драйвера.")})
        {
            var item=work.Results.FirstOrDefault(x=>x.Title==key);
            string current=item?.Evidence switch {null=>work.LastScan.HasValue?"Недоступно":"До проверки","Системное значение"=>reference+" (штатно)",var n=>n};
            var p=Card(BaselineRows);
            Line(p,key,17);
            var pair=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};
            p.Children.Add(pair);Line(pair,"Текущее · "+current);Line(pair,"Microsoft Default · "+reference);
            Line(p,reason);
        }
    }
    void QueryHint(){ Placeholder.Visibility=string.IsNullOrEmpty(Query.Text)?Visibility.Visible:Visibility.Collapsed; }
    T R<T>(string key)=>(T)FindResource(key);
    StackPanel Card(Panel parent)
    {
        var stack=new StackPanel();
        var grid=new Grid();
        grid.Children.Add(new Border {Background=R<Brush>("Engraving"),Opacity=.16,IsHitTestVisible=false});
        grid.Children.Add(stack);
        parent.Children.Add(new Border {Style=R<Style>("Panel"),Child=grid});
        return stack;
    }
    static TextBlock Line(Panel p,string text,int size=14)
    {
        var t=new TextBlock {Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,4),
            Foreground=new SolidColorBrush(size>15?Color.FromRgb(245,233,206):Color.FromRgb(205,200,190))};
        p.Children.Add(t);return t;
    }
    static Button Button(string label,Action action)
    {
        var b=new Button{Content=label,HorizontalAlignment=HorizontalAlignment.Left};
        b.Click+=(_,_)=>action();return b;
    }
    void Active(Button b,bool selected)
    {
        if(selected){b.Background=R<Brush>("Gold"); b.Foreground=Brushes.Black;}
        else{b.ClearValue(BackgroundProperty);b.ClearValue(ForegroundProperty);}
    }
    void Help(object sender,RoutedEventArgs e)=>Select(DocsTab);
    void Minimize(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
    void Maximize(object sender,RoutedEventArgs e)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    void ExitWindow(object sender,RoutedEventArgs e)=>Close();
    void Nav(object sender,RoutedEventArgs e)=>Select((Button)sender);
    void Select(Button active)
    {
        var tabs=new[]{ScanTab,DocsTab,ReportTab};
        foreach(var b in tabs) b.Foreground=b==active?Brushes.Black:Brushes.AntiqueWhite;
        PillOffset.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(174*Array.IndexOf(tabs,active),TimeSpan.FromMilliseconds(310)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseInOut}});
        Scanner.Visibility=active==ScanTab?Visibility.Visible:Visibility.Collapsed;
        Docs.Visibility=active==DocsTab?Visibility.Visible:Visibility.Collapsed;
        Report.Visibility=active==ReportTab?Visibility.Visible:Visibility.Collapsed;
        if(active==ReportTab) RefreshReport();
        Body.BeginAnimation(OpacityProperty,new DoubleAnimation(.25,1,TimeSpan.FromMilliseconds(220)));
        Body.RenderTransform.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(9,0,TimeSpan.FromMilliseconds(260)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
    }
    void Search(object sender,TextChangedEventArgs e){if(Articles!=null) RenderArticles();}
    void RenderArticles()
    {
        foreach(Button b in Filters.Children) Active(b,(string)b.Content==category);
        Articles.Children.Clear();
        var matches=work.Search(category,Query.Text).ToList();
        ArticleCount.Text=$"Статей: {matches.Count} / {KnowledgeBaseService.All.Length}";
        if(matches.Count==0) Line(Articles,"Совпадений нет. Измените запрос или выберите другой раздел.");
        foreach(var a in matches)
        {
            var p=Card(Articles);
            Line(p,a.App+"   ·   "+a.Tags).Foreground=R<Brush>("Gold");
            var details=new StackPanel{Visibility=Visibility.Collapsed,Margin=new Thickness(0,8,0,0)};
            var toggle=Button("＋   "+a.Title,()=>{bool show=details.Visibility!=Visibility.Visible;details.Visibility=show?Visibility.Visible:Visibility.Collapsed;});
            p.Children.Add(toggle);
            p.Children.Add(details);
            for(int i=0;i<a.Steps.Length;i++) Line(details,(i+1)+".  "+a.Steps[i]);
            var link=Button("Источник  ↗",()=>{try{Process.Start(new ProcessStartInfo(a.Source){UseShellExecute=true});}catch(Exception ex){Notify(ex.Message);}});
            details.Children.Add(link);
        }
    }
    async void StartScan(object sender,RoutedEventArgs e)=>await Scan();
    async Task Scan()
    {
        if(scanning)return;
        scanning=true;Start.IsEnabled=Elevate.IsEnabled=false; Cards.IsEnabled=false;
        DialLabel.Text="ПОИСК"; Dial.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromSeconds(2)){RepeatBehavior=RepeatBehavior.Forever});
        Lamp.BeginAnimation(OpacityProperty,new DoubleAnimation(.2,1,TimeSpan.FromMilliseconds(550)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever});
        try{
            var progress=new Progress<string>(s=>State.Text="Проверка · "+s);
            work.Results=await Task.Run(()=>DiagnosticsService.Scan(progress));
            work.LastScan=DateTime.Now;
            FillBaseline();
            ResultHeading.Text="Результаты проверки";Cards.Children.Clear();
            foreach(var f in work.Results.OrderBy(x=>x.Status switch{"Риск фризов"=>0,"Не проверено"=>1,"Справочно"=>2,_=>3}))
            {
                var p=Card(Cards);
                Line(p,f.Status).Foreground=f.Status=="Риск фризов"?Brushes.LightSalmon:f.Status=="Норма"?Brushes.PaleGreen:R<Brush>("Gold");
                Line(p,f.Title,19);Line(p,f.Evidence);Line(p,f.Meaning);
                if(f.CanRestore)p.Children.Add(Button("Восстановить по умолчанию",()=>Restore(f)));
            }
            var rec=Card(Cards);Line(rec,"Что проверить вручную",19);
            foreach(var hint in Hints) Line(rec,"•  "+hint);
            State.Text=$"Проверка завершена в {work.LastScan:HH:mm}   ·   {work.Results.Count} результатов";
            RefreshReport();
        } catch(Exception ex){State.Text="Ошибка проверки";Notify(ex.Message);}
        finally{DialLabel.Text="ГОТОВ";Dial.BeginAnimation(RotateTransform.AngleProperty,null);scanning=false;Start.IsEnabled=Elevate.IsEnabled=Cards.IsEnabled=true;Lamp.BeginAnimation(OpacityProperty,null);}
    }
    static readonly string[] Hints=[
        "При нестабильности после включения проверьте Fast Boot в UEFI. Запишите исходное значение, измените один параметр и повторите холодный запуск.",
        "Если тормозит Steam или Discord, временно верните штатное оформление, приостановите дополнения и повторите проблемное действие.",
        "При задержках видео в браузере сравните воспроизведение с расширением и после его отключения. Меняйте одно расширение за раз."
    ];
    void Notify(string msg)=>MessageBox.Show(this,msg,"mpws",MessageBoxButton.OK,MessageBoxImage.Information);
    void Admin(object sender,RoutedEventArgs e)
    {
        try{Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--scan"){UseShellExecute=true,Verb="runas"});}
        catch(Exception ex){Notify(ex.Message);}
    }
    async void Restore(Finding f)
    {
        if(f.Repair==null){Notify(f.Guide??"Для этой настройки используется системное значение.");return;}
        if(!DiagnosticsService.IsAdmin){Notify("Для записи снимка и изменения параметра откройте расширенную проверку.");return;}
        if(MessageBox.Show(this,$"Сохранить {f.Repair} = {f.Evidence} и удалить переопределение? { (Recovery.IsTimer(f.Repair) ? "При шифровании диска убедитесь, что у вас есть ключ восстановления BitLocker." : "") } Windows применит своё значение после перезагрузки. Снимок будет в разделе отката.","mpws · восстановление",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        try{Recovery.Apply(f.Repair,false,Recovery.IsTimer(f.Repair)?1:int.Parse(f.Evidence));await Scan();}
        catch(Exception ex){Notify(ex.Message);RefreshReport();}
    }
    void RefreshReport()
    {
        string Count(Func<Finding,bool> predicate)=>work.LastScan.HasValue?work.Results.Count(predicate).ToString():"—";
        ReportTiles.ItemsSource=new Tile[]{
            new("Результаты",Count(_=>true),"Все строки последнего сканирования.",ServicesIcon),
            new("Риски фризов",Count(f=>f.Status=="Риск фризов"),"Параметры, требующие проверки под нагрузкой.",ScreenIcon),
            new("Не проверено",Count(f=>f.Status=="Не проверено"),"Проверки, для которых не удалось получить данные.",ClockIcon)
        };
        Save.IsEnabled=work.LastScan.HasValue;
        ReportInfo.Text=work.LastScan.HasValue?$"{Environment.MachineName}   ·   {work.LastScan:g}   ·   результатов: {work.Results.Count}, рисков: {work.Results.Count(f=>f.Status=="Риск фризов")}, недоступно: {work.Results.Count(f=>f.Status=="Не проверено")}":"Запустите проверку, чтобы сохранить результаты.";
        History.Children.Clear();
        SummaryRows.Children.Clear();
        if(work.LastScan.HasValue)
        {
            Line(SummaryRows,"Результаты последней проверки",21);
            foreach(var f in work.Results)
                Line(SummaryRows,f.Title+"   ·   "+f.Status+"   ·   "+f.Evidence);
        }
        try{
            var entries=Recovery.Pending();
            if(entries.Count==0)Line(History,"Сохранённых изменений MPWS пока нет.");
            foreach(var c in entries)
            {
                var p=Card(History);Line(p,c.Name,18);Line(p,$"Исходное значение: {c.Original}   ·   {c.Created:g}");
                p.Children.Add(Button("Вернуть исходное значение",async ()=>{
                    if(!DiagnosticsService.IsAdmin){Notify("Откройте расширенную проверку для отката.");return;}
                    if(MessageBox.Show(this,$"Вернуть {c.Name} = {c.Original}? После отката перезагрузите ПК.","mpws · откат",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                    try{Recovery.Apply(c.Name,true,c.Original);await Scan();}catch(Exception ex){Notify(ex.Message);}
                }));
            }
        }catch(Exception ex){Line(History,ex.Message);}
    }
    void Export(object sender,RoutedEventArgs e)
    {
        var dlg=new SaveFileDialog{FileName=$"mpws-{DateTime.Now:yyyyMMdd-HHmm}.txt",Filter="Текстовый отчёт|*.txt"};
        if(dlg.ShowDialog(this)!=true)return;
        try{File.WriteAllText(dlg.FileName,ReportInfo.Text+"\r\n\r\n"+string.Join("\r\n\r\n",work.Results.Select(f=>$"{f.Title} · {f.Status}\r\n{f.Evidence}\r\n{f.Meaning}"))+"\r\n\r\n"+string.Join("\r\n",Hints),Encoding.UTF8);State.Text="Отчёт сохранён";}
        catch(Exception ex){Notify(ex.Message);}
    }
}


