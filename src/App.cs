using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

[assembly:AssemblyTitle("一行 Reader")]
[assembly:AssemblyDescription("单行小说阅读器与 Reader 同步客户端")]
[assembly:AssemblyVersion("1.0.4.0")]
[assembly:System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8",FrameworkDisplayName=".NET Framework 4.8")]
namespace OneLine {
 public class BookRow {
  public Book Book{get;set;}
  public string Title{get{return Book.Title;}}
  public string Badge{get{return Book.Format.ToUpperInvariant();}}
  public string Subtitle{get{return (string.IsNullOrWhiteSpace(Book.Author)?"作者未标注":Book.Author)+"  ·  "+(Book.Remote?"Reader":"本地");}}
  public string Position{get{return Book.Position.UpdatedAt>0?"第 "+(Book.Position.Chapter+1)+" 节":"未读";}}
 }
 public class Hotkeys:IDisposable {
  [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterHotKey(IntPtr h,int id,uint mods,uint key);
  [DllImport("user32.dll")]static extern bool UnregisterHotKey(IntPtr h,int id);
  readonly HwndSource source;readonly IntPtr handle;readonly Action<int> action;
  readonly Dictionary<int,string> registered=new Dictionary<int,string>();
  bool suspended;
  public string Report{get;private set;}
  public Hotkeys(Action<int> callback){
   // A dedicated message-only window keeps the hotkey receiver alive when either
   // visible window is hidden, closed to the tray, or has not yet been shown.
   var options=new HwndSourceParameters("OneLineReader.GlobalHotkeys"){ParentWindow=new IntPtr(-3),WindowStyle=0,Width=0,Height=0};
   source=new HwndSource(options);handle=source.Handle;action=callback;source.AddHook(Hook);
  }
  IntPtr Hook(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled){if(msg==0x312){handled=true;action(w.ToInt32());}return IntPtr.Zero;}
  static void Parse(string s,out uint mods,out uint key){
   mods=0x4000;key=0;var parts=(s??"").Split('+');Key mainKey=Key.None;
   foreach(string token in parts){string p=token.Trim();if(p.Equals("Alt",StringComparison.OrdinalIgnoreCase))mods|=1;else if(p.Equals("Ctrl",StringComparison.OrdinalIgnoreCase)||p.Equals("Control",StringComparison.OrdinalIgnoreCase))mods|=2;else if(p.Equals("Shift",StringComparison.OrdinalIgnoreCase))mods|=4;else{Key k;if(!Enum.TryParse<Key>(p,true,out k)||k==Key.None||key!=0)throw new Exception("无法识别快捷键："+s);mainKey=k;key=(uint)KeyInterop.VirtualKeyFromKey(k);}}
   if(key==0)throw new Exception("请按下功能键，或带 Ctrl / Alt / Shift 的组合键。");
   if((mods&7)==0&&(mainKey<Key.F1||mainKey>Key.F24))throw new Exception("单独按键支持 F1–F24；其他按键请使用 Ctrl / Alt / Shift 组合。");
   if(mainKey==Key.F12)throw new Exception("F12 被 Windows 保留，请使用 F1、F2 或其他功能键。");
  }
  static string Identity(string key){uint mods,code;Parse(key,out mods,out code);return mods+":"+code;}
  bool Register(int id,string key){uint mods,code;Parse(key,out mods,out code);if(!RegisterHotKey(handle,id,mods,code))return false;registered[id]=key;return true;}
  public void Suspend(){if(suspended)return;foreach(int id in registered.Keys)UnregisterHotKey(handle,id);suspended=true;}
  public void Resume(){
   if(!suspended)return;suspended=false;var saved=registered.ToArray();registered.Clear();var errors=new List<string>();
   foreach(var pair in saved)if(!Register(pair.Key,pair.Value))errors.Add(pair.Value+" 未能恢复注册，请重新保存快捷键。");
   if(errors.Count>0)Report=string.Join("\n",errors);
  }
  public string Set(KeysConfig c){
   string[] keys={c.Boss,c.Next,c.Previous,c.Manager};
   uint[] mods=new uint[4],codes=new uint[4];for(int i=0;i<4;i++)Parse(keys[i],out mods[i],out codes[i]);
   if(keys.Select(Identity).Distinct().Count()!=4)throw new Exception("快捷键不能重复。");
   var previous=new Dictionary<int,string>(registered);var warnings=new List<string>();string[] labels={"老板键","下一行","上一行","控制台"};
   for(int i=1;i<=4;i++)UnregisterHotKey(handle,i);
   registered.Clear();suspended=false;
   for(int i=0;i<4;i++){
    if(Register(i+1,keys[i]))continue;
    string requested=keys[i],old;
    // A failed page-turn shortcut must never disable an already working boss key.
    bool restored=previous.TryGetValue(i+1,out old)&&!keys.Where((k,index)=>index!=i).Any(k=>Identity(k)==Identity(old))&&Register(i+1,old);
    if(restored){keys[i]=old;warnings.Add(labels[i]+" "+requested+" 被占用，已保留 "+old+"。");continue;}
    if(i==0){
     foreach(string candidate in new[]{"F8","Ctrl+Alt+F8","Ctrl+Shift+F8"}){
      if(keys.Skip(1).Any(k=>Identity(k)==Identity(candidate)))continue;
      if(Register(1,candidate)){keys[0]=candidate;restored=true;warnings.Add("老板键 "+requested+" 被占用，已改用 "+candidate+"。");break;}
     }
    }
    if(!restored)warnings.Add(labels[i]+" "+requested+" 未能注册，请更换按键。");
   }
   c.Boss=keys[0];c.Next=keys[1];c.Previous=keys[2];c.Manager=keys[3];
   Report=warnings.Count==0?"4 个全局快捷键均已启用。":string.Join("\n",warnings);return Report;
  }
  public void Dispose(){for(int i=1;i<=4;i++)UnregisterHotKey(handle,i);source.RemoveHook(Hook);source.Dispose();}
 }
 public class Strip:Window {
  readonly ReadingController reader;readonly Action manager,hide,toc,resize;readonly TextBlock text;readonly Border surface;readonly Border progress;readonly Grid grid;readonly Thumb thumb;
  Point down;bool moving,dragged;
  public Strip(ReadingController r,Action showManager,Action bossHide,Action showToc,Action resized){
   reader=r;manager=showManager;hide=bossHide;toc=showToc;resize=resized;
   Title="一行阅读";WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.Manual;
   grid=new Grid();surface=new Border{CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(0)};grid.Children.Add(surface);
   text=new TextBlock{Margin=new Thickness(16,0,24,0),VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.NoWrap,ClipToBounds=true,IsHitTestVisible=false};grid.Children.Add(text);
   progress=new Border{Height=2,Background=new SolidColorBrush(Color.FromRgb(108,164,127)),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom,CornerRadius=new CornerRadius(1)};grid.Children.Add(progress);
   thumb=new Thumb{Width=12,Height=12,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Cursor=Cursors.SizeWE,Opacity=0.12,Background=Brushes.Gray};grid.Children.Add(thumb);Content=grid;
   thumb.DragDelta+=(s,e)=>{Width=Store.Clamp(Width+e.HorizontalChange,240,Math.Min(2400,SystemParameters.VirtualScreenWidth));reader.Store.State.Appearance.Width=Width;reader.Reflow();};thumb.DragCompleted+=(s,e)=>{reader.Store.Save();resize();};
   MouseLeftButtonDown+=(s,e)=>{if(e.OriginalSource is Thumb)return;down=e.GetPosition(this);moving=true;dragged=false;CaptureMouse();e.Handled=true;};
   MouseMove+=(s,e)=>{if(!moving)return;var p=e.GetPosition(this);if(Math.Abs(p.X-down.X)>4||Math.Abs(p.Y-down.Y)>4||dragged){dragged=true;Left+=p.X-down.X;Top+=p.Y-down.Y;}};
   MouseLeftButtonUp+=async(s,e)=>{if(!moving)return;moving=false;ReleaseMouseCapture();if(dragged){reader.Store.State.Appearance.X=Left;reader.Store.State.Appearance.Y=Top;reader.Store.Save();}else await Run(()=>reader.Turn(1));};
   MouseWheel+=async(s,e)=>{e.Handled=true;await Run(()=>reader.Turn(e.Delta<0?1:-1));};
   PreviewKeyDown+=async(s,e)=>{if(e.Key==Key.Escape){hide();e.Handled=true;return;}if(Keyboard.Modifiers!=ModifierKeys.None)return;if(e.Key==Key.Right||e.Key==Key.Down||e.Key==Key.Space||e.Key==Key.PageDown){e.Handled=true;await Run(()=>reader.Turn(1));}else if(e.Key==Key.Left||e.Key==Key.Up||e.Key==Key.PageUp){e.Handled=true;await Run(()=>reader.Turn(-1));}};
   MouseRightButtonUp+=(s,e)=>{BuildMenu();ContextMenu.IsOpen=true;e.Handled=true;};
   MouseEnter+=(s,e)=>thumb.Opacity=0.6;MouseLeave+=(s,e)=>thumb.Opacity=0.12;
   reader.Changed+=Render;Apply();
  }
  async Task Run(Func<Task> f){try{await f();}catch(Exception ex){reader.Pause();reader.SetStatus(ex.Message);}}
  void BuildMenu(){
   var menu=new ContextMenu();MenuItem m;
   m=new MenuItem{Header="上一行"};m.Click+=async(s,e)=>await Run(()=>reader.Turn(-1));menu.Items.Add(m);
   m=new MenuItem{Header="下一行"};m.Click+=async(s,e)=>await Run(()=>reader.Turn(1));menu.Items.Add(m);
   m=new MenuItem{Header="章节目录…"};m.Click+=(s,e)=>toc();menu.Items.Add(m);
   m=new MenuItem{Header="上一章"};m.Click+=async(s,e)=>{if(reader.Current!=null)await Run(()=>reader.Jump(reader.Current.Position.Chapter-1,0));};menu.Items.Add(m);
   m=new MenuItem{Header="下一章"};m.Click+=async(s,e)=>{if(reader.Current!=null)await Run(()=>reader.Jump(reader.Current.Position.Chapter+1,0));};menu.Items.Add(m);
   menu.Items.Add(new Separator());m=new MenuItem{Header=reader.Auto?"暂停自动翻页":"自动翻页（"+reader.Store.State.AutoSeconds+" 秒）"};m.Click+=(s,e)=>reader.ToggleAuto();menu.Items.Add(m);
   m=new MenuItem{Header="立即同步进度"};m.Click+=async(s,e)=>await Run(()=>reader.Sync(true));menu.Items.Add(m);
   menu.Items.Add(new Separator());m=new MenuItem{Header="打开控制台"};m.Click+=(s,e)=>manager();menu.Items.Add(m);m=new MenuItem{Header="隐藏"};m.Click+=(s,e)=>hide();menu.Items.Add(m);
   ContextMenu=menu;
  }
  public void Apply(){
   var a=reader.Store.State.Appearance;Store.ValidateAppearance(a);Width=a.Width;Height=Math.Max(a.Height,a.FontSize*1.5+8);Opacity=a.Opacity;Topmost=a.Topmost;text.FontFamily=new FontFamily(a.Font);text.FontSize=a.FontSize;
   text.Foreground=ColorBrush(a.TextColor,1);
   surface.Background=BackgroundBrush(a);
   Render();
  }
  public static Brush BackgroundBrush(Appearance appearance){
   // Keep at least one alpha step after the window's overall opacity is applied.
   int minimumAlpha=(int)Math.Ceiling(1.0/appearance.Opacity);
   return ColorBrush(appearance.Background,appearance.BackgroundOpacity,minimumAlpha);
  }
  public static Brush ColorBrush(string color,double opacity,int minimumAlpha=0){
   try{
    Color value=(Color)ColorConverter.ConvertFromString(color);
    value.A=(byte)Math.Max(minimumAlpha,(int)(Store.Clamp(opacity,0,1)*255));
    return new SolidColorBrush(value);
   }catch{return Brushes.Gray;}
  }
  public void EnsureVisible(bool reset=false){
   var a=reader.Store.State.Appearance;Rect area=SystemParameters.WorkArea;
   if(reset){a.X=area.Left+50;a.Y=area.Bottom-Height-65;}
   Rect wanted=new Rect(a.X,a.Y,Math.Min(Width,area.Width),Height);
   // Check all monitors; a disconnected monitor must not strand the reading strip.
   bool intersects=false;var scale=PresentationSource.FromVisual(this);double dx=1,dy=1;if(scale!=null){dx=scale.CompositionTarget.TransformToDevice.M11;dy=scale.CompositionTarget.TransformToDevice.M22;}
   foreach(var screen in Forms.Screen.AllScreens){var bounds=screen.WorkingArea;var r=new Rect(bounds.X/dx,bounds.Y/dy,bounds.Width/dx,bounds.Height/dy);if(r.Contains(new Point(wanted.X+24,wanted.Y+16)))intersects=true;}
   if(!intersects){a.X=area.Left+40;a.Y=area.Bottom-Height-65;}Left=a.X;Top=a.Y;
  }
  public void Render(){text.Text=reader.Line;progress.Visibility=reader.Store.State.Appearance.Progress?Visibility.Visible:Visibility.Collapsed;progress.Width=Math.Max(0,(Width-4)*reader.Percentage);ToolTip=reader.Current==null?"右键打开控制台":reader.Current.Title+" · "+reader.Chapter.Title+" · "+reader.Percentage.ToString("P1");}
  public void CloseMenu(){if(ContextMenu!=null)ContextMenu.IsOpen=false;}
 }
 public class DesktopApp {
  readonly Application app;readonly Store store;readonly ReaderClient api;readonly ReadingController reader;readonly Window main;readonly Strip strip;readonly Forms.NotifyIcon tray;readonly Hotkeys hotkeys;readonly DispatcherTimer timer;
  bool quitting,hidden,loadingSettings=true,networkBusy,ticking,dirtySettings;long settingsAt;int page=1,total;List<Book> remoteBooks=new List<Book>();Window tocWindow;
  T Find<T>(string name) where T:class{return main.FindName(name) as T;}
  void Click(string name,Action action){Find<Button>(name).Click+=(s,e)=>{try{action();}catch(Exception ex){Error(ex);}};}
  void ClickAsync(string name,Func<Task> action){Find<Button>(name).Click+=async(s,e)=>await Run(action);}
  async Task Run(Func<Task> f){try{await f();}catch(Exception ex){Error(ex);}}
  void Error(Exception ex){reader.SetStatus(ex.Message);if(main.IsVisible)MessageBox.Show(main,ex.Message,"一行 Reader",MessageBoxButton.OK,MessageBoxImage.Information);}
  public DesktopApp(Application application,string root,bool smoke=false){
   app=application;store=new Store(root);api=new ReaderClient(store);reader=new ReadingController(store,api);
   if(smoke)store.State.Keys=new KeysConfig{Boss="Ctrl+Alt+F8",Next="Ctrl+Alt+F9",Previous="Ctrl+Alt+F10",Manager="Ctrl+Alt+F11"};
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml"))main=(Window)XamlReader.Load(stream);
   app.MainWindow=main;main.Closing+=(s,e)=>{if(!quitting){e.Cancel=true;HideMain();}};
   strip=new Strip(reader,ShowMain,BossHide,ShowToc,LoadAppearance);strip.EnsureVisible();
   hotkeys=new Hotkeys(Hotkey);string keyReport;try{keyReport=hotkeys.Set(store.State.Keys);store.Save();}catch(Exception ex){keyReport=ex.Message;}
   tray=new Forms.NotifyIcon{Text="一行 Reader",Icon=System.Drawing.SystemIcons.Application,Visible=true};
   var trayMenu=new Forms.ContextMenuStrip();trayMenu.Items.Add("显示控制台",null,(s,e)=>app.Dispatcher.Invoke(new Action(ShowMain)));trayMenu.Items.Add("隐藏 / 恢复阅读条",null,(s,e)=>app.Dispatcher.Invoke(new Action(BossToggle)));trayMenu.Items.Add("退出一行 Reader",null,(s,e)=>app.Dispatcher.Invoke(new Action(async()=>await Quit())));tray.ContextMenuStrip=trayMenu;tray.DoubleClick+=(s,e)=>app.Dispatcher.Invoke(new Action(ShowMain));
   Wire();LoadAppearance();LoadConnection();LoadKeys();Find<TextBlock>("HotkeyStatus").Text=keyReport;loadingSettings=false;
   reader.Changed+=UpdateCurrent;reader.StatusChanged+=()=>{Find<TextBlock>("StatusLabel").Text=reader.Status;Find<TextBlock>("SyncDetail").Text=reader.Status;Find<TextBlock>("ConnectionLabel").Text=ConnectionText();};
   RefreshLocal();UpdateCurrent();
   timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};timer.Tick+=async(s,e)=>{if(ticking||quitting)return;ticking=true;try{if(dirtySettings&&Store.Now-settingsAt>800){store.Save();dirtySettings=false;}await reader.Tick(strip.IsVisible&&!hidden);}catch(Exception ex){reader.SetStatus("保存失败："+ex.Message);}finally{ticking=false;}};timer.Start();
   main.StateChanged+=(s,e)=>{if(main.WindowState==WindowState.Minimized)reader.RequestSync();};
   main.Show();if(store.Warning.Length>0)reader.SetStatus(store.Warning);
   if(!string.IsNullOrEmpty(store.State.LastBook)){
    var b=store.State.Books.FirstOrDefault(x=>x.Scope+"|"+x.Id==store.State.LastBook&&(!x.Remote||x.Scope==store.State.Account.Scope));
    if(b!=null)main.Dispatcher.BeginInvoke(new Action(async()=>await Run(async()=>{await reader.Open(b);UpdateCurrent();})));
   }
  }
  string ConnectionText(){return api.SignedIn?"已登录："+store.State.Account.Username+"  ·  "+store.State.Account.Server:"尚未登录";}
  void Wire(){
   Click("NavBooks",()=>View("Books"));Click("NavAppearance",()=>View("Appearance"));Click("NavKeys",()=>View("Keys"));Click("NavReader",()=>View("Reader"));Click("HideButton",HideMain);
   ClickAsync("DemoButton",async()=>{var b=store.Demo();store.Save();await Open(b);RefreshLocal();});
   ClickAsync("ImportButton",Import);ClickAsync("RefreshButton",RefreshBooks);ClickAsync("ReadButton",ReadSelected);Click("TocButton",ShowToc);ClickAsync("SyncButton",()=>reader.Sync(true));
   ClickAsync("KeepLocal",()=>reader.ResolveConflict(false));ClickAsync("UseCloud",()=>reader.ResolveConflict(true));
   Find<ListBox>("BookList").MouseDoubleClick+=async(s,e)=>await Run(ReadSelected);
   Find<TextBox>("SearchBox").KeyDown+=async(s,e)=>{if(e.Key==Key.Enter){page=1;await Run(RefreshBooks);}};
   Find<ComboBox>("SourceBox").SelectionChanged+=async(s,e)=>{page=1;await Run(RefreshBooks);};
   ClickAsync("PrevBooks",async()=>{page=Math.Max(1,page-1);await RefreshBooks();});ClickAsync("NextBooks",async()=>{page++;await RefreshBooks();});
   ClickAsync("TestConnection",async()=>await Network(async()=>{string result=await api.Probe(Find<TextBox>("ServerBox").Text);Find<TextBlock>("ConnectionLabel").Text="连接成功："+result;}));
   ClickAsync("ConnectButton",async()=>await Network(async()=>{
    reader.Pause();if(reader.Current!=null&&reader.Current.Remote){await reader.Sync(false);reader.Save();}
    await api.Login(Find<TextBox>("ServerBox").Text,Find<TextBox>("UsernameBox").Text,Find<PasswordBox>("PasswordBox").Password);Find<PasswordBox>("PasswordBox").Clear();LoadConnection();
    if(reader.Current!=null&&reader.Current.Remote&&reader.Current.Scope!=store.State.Account.Scope){reader.Current=null;reader.Pager=null;strip.Hide();UpdateCurrent();}
    reader.SetStatus("Reader 登录成功");View("Books");Find<ComboBox>("SourceBox").SelectedIndex=1;page=1;await RefreshBooks();
   }));
   ClickAsync("LogoutButton",async()=>await Network(async()=>{reader.Pause();if(reader.Current!=null&&reader.Current.Remote){await reader.Sync(false);strip.Hide();reader.Current=null;reader.Pager=null;reader.Conflict=null;UpdateCurrent();}try{await api.Logout();}finally{Find<PasswordBox>("PasswordBox").Clear();LoadConnection();reader.SetStatus("已退出登录。本机缓存与未同步位置保留。");}}));
   Find<TextBox>("ServerBox").TextChanged+=(s,e)=>Find<TextBlock>("HttpNotice").Visibility=Find<TextBox>("ServerBox").Text.StartsWith("http://",StringComparison.OrdinalIgnoreCase)?Visibility.Visible:Visibility.Collapsed;
   foreach(string name in new[]{"FontSlider","WidthSlider","HeightSlider","OpacitySlider","BgSlider"})Find<Slider>(name).ValueChanged+=(s,e)=>AppearanceChanged();
   Find<ComboBox>("FontBox").SelectionChanged+=(s,e)=>{if(!loadingSettings)main.Dispatcher.BeginInvoke(new Action(AppearanceChanged));};
   Find<ComboBox>("FontBox").LostKeyboardFocus+=(s,e)=>AppearanceChanged();
   foreach(string name in new[]{"TextColorBox","BgColorBox"})Find<TextBox>(name).LostKeyboardFocus+=(s,e)=>AppearanceChanged();
   Find<CheckBox>("TopCheck").Click+=(s,e)=>AppearanceChanged();Find<CheckBox>("ProgressCheck").Click+=(s,e)=>AppearanceChanged();
   Click("TextColorPick",()=>PickColor("TextColorBox"));Click("BgColorPick",()=>PickColor("BgColorBox"));
   Click("PresetDark",()=>Preset("#DCE6DF","#192A24",0.8));Click("PresetPaper",()=>Preset("#514A3C","#F5EEDB",0.9));Click("PresetQuiet",()=>Preset("#73777A","#F3F3F3",Appearance.MinimumBackgroundOpacity));Click("ResetPosition",()=>{strip.EnsureVisible(true);store.Save();if(reader.Current!=null)strip.Show();});
   foreach(string name in new[]{"BossKeyBox","NextKeyBox","PrevKeyBox","ManagerKeyBox"}){
    var input=Find<TextBox>(name);input.PreviewKeyDown+=CaptureShortcut;
    input.GotKeyboardFocus+=(s,e)=>{hotkeys.Suspend();Find<TextBlock>("HotkeyStatus").Text="正在录入按键；离开输入框后恢复已保存的全局快捷键。";};
    input.LostKeyboardFocus+=(s,e)=>main.Dispatcher.BeginInvoke(new Action(()=>{
     var focused=Keyboard.FocusedElement as TextBox;
     if(focused!=null&&new[]{"BossKeyBox","NextKeyBox","PrevKeyBox","ManagerKeyBox"}.Contains(focused.Name))return;
     hotkeys.Resume();Find<TextBlock>("HotkeyStatus").Text=hotkeys.Report;
    }));
   }
   Click("SaveKeys",()=>{var next=new KeysConfig{Boss=Find<TextBox>("BossKeyBox").Text,Next=Find<TextBox>("NextKeyBox").Text,Previous=Find<TextBox>("PrevKeyBox").Text,Manager=Find<TextBox>("ManagerKeyBox").Text};string report=hotkeys.Set(next);store.State.Keys=next;store.Save();LoadKeys();Find<TextBlock>("HotkeyStatus").Text=report;reader.SetStatus(report);});
   Click("SaveAuto",()=>{int value;if(!int.TryParse(Find<TextBox>("AutoSecondsBox").Text,out value)||value<1||value>3600)throw new Exception("自动翻页间隔请输入 1–3600 秒。");store.State.AutoSeconds=value;store.Save();reader.SetStatus("自动翻页间隔已保存");});
   View("Books");
  }
  void CaptureShortcut(object sender,KeyEventArgs e){
   e.Handled=true;Key key=e.Key==Key.System?e.SystemKey:e.Key;
   if(new[]{Key.LeftAlt,Key.RightAlt,Key.LeftCtrl,Key.RightCtrl,Key.LeftShift,Key.RightShift}.Contains(key))return;
   var mods=Keyboard.Modifiers;string prefix="";if((mods&ModifierKeys.Control)!=0)prefix+="Ctrl+";if((mods&ModifierKeys.Alt)!=0)prefix+="Alt+";if((mods&ModifierKeys.Shift)!=0)prefix+="Shift+";if(prefix.Length>0||(key>=Key.F1&&key<=Key.F24))((TextBox)sender).Text=prefix+key;
  }
  async Task Network(Func<Task> f){
   if(networkBusy)return;networkBusy=true;foreach(string n in new[]{"ConnectButton","TestConnection","LogoutButton"})Find<Button>(n).IsEnabled=false;
   try{await f();}finally{networkBusy=false;foreach(string n in new[]{"ConnectButton","TestConnection","LogoutButton"})Find<Button>(n).IsEnabled=true;}
  }
  void View(string view){foreach(string n in new[]{"Books","Appearance","Keys","Reader"}){Find<FrameworkElement>(n+"View").Visibility=view==n?Visibility.Visible:Visibility.Collapsed;Find<Button>("Nav"+n).Background=view==n?Strip.ColorBrush("#315B44",1):Brushes.Transparent;}}
  public void ShowMain(){hidden=false;main.Show();main.WindowState=WindowState.Normal;main.Activate();reader.RequestSync(true);}
  void HideMain(){main.Hide();reader.RequestSync();}
  void BossHide(){hidden=true;reader.Pause();strip.CloseMenu();strip.ReleaseMouseCapture();strip.Hide();main.Hide();if(tocWindow!=null)tocWindow.Hide();if(tray.ContextMenuStrip!=null)tray.ContextMenuStrip.Close();foreach(Window w in app.Windows)if(w!=strip&&w!=main)w.Hide();reader.RequestSync();}
  void BossToggle(){if(hidden||(!strip.IsVisible&&!main.IsVisible)){hidden=false;if(reader.Current!=null){strip.EnsureVisible();strip.Show();reader.RequestSync(true);}else ShowMain();}else BossHide();}
  async void Hotkey(int id){if(quitting)return;switch(id){case 1:BossToggle();break;case 2:if(!hidden&&strip.IsVisible)await Run(()=>reader.Turn(1));break;case 3:if(!hidden&&strip.IsVisible)await Run(()=>reader.Turn(-1));break;case 4:ShowMain();break;}}
  async Task Open(Book b){await reader.Open(b);hidden=false;strip.Apply();strip.EnsureVisible();strip.Show();strip.Activate();UpdateCurrent();}
  async Task ReadSelected(){var row=Find<ListBox>("BookList").SelectedItem as BookRow;var b=row==null?reader.Current:row.Book;if(b==null)throw new Exception("请先选择一本书，或导入 TXT / EPUB。");await Open(b);}
  async Task Import(){
   var dialog=new Microsoft.Win32.OpenFileDialog{Title="导入小说",Filter="小说文件|*.txt;*.epub|TXT|*.txt|EPUB|*.epub",Multiselect=true};if(dialog.ShowDialog(main)!=true)return;
   Book last=null;var errors=new List<string>();reader.SetStatus("正在导入…");
   foreach(string path in dialog.FileNames){try{last=TextBook.Import(path,store);}catch(Exception ex){errors.Add(Path.GetFileName(path)+"："+ex.Message);}}
   Find<ComboBox>("SourceBox").SelectedIndex=0;RefreshLocal();if(last!=null)await Open(last);if(errors.Count>0)throw new Exception(string.Join("\n",errors));
  }
  void RefreshLocal(){if(Find<ComboBox>("SourceBox").SelectedIndex!=0)return;string query=Find<TextBox>("SearchBox").Text.Trim();SetBooks(store.State.Books.Where(b=>!b.Remote&&(b.Title+" "+b.Author).IndexOf(query,StringComparison.CurrentCultureIgnoreCase)>=0).ToList(),false);}
  int fetchVersion;
  async Task RefreshBooks(){
   int source=Find<ComboBox>("SourceBox").SelectedIndex,version=++fetchVersion;
   if(source==0){RefreshLocal();return;}
   if(!api.SignedIn){SetBooks(new List<Book>(),false);View("Reader");reader.SetStatus("请登录 Reader 后获取书架。");return;}
   string scope=store.State.Account.Scope,search=Find<TextBox>("SearchBox").Text.Trim();int requestedPage=page;
   Find<Button>("RefreshButton").IsEnabled=false;
   try{
    reader.SetStatus("正在获取 Reader 书籍…");var result=await api.Shelf(source==1?"shelf":"library",search,requestedPage);
    if(version!=fetchVersion||scope!=store.State.Account.Scope)return;
    remoteBooks=new List<Book>();foreach(var book in result.Books){var existing=store.State.Books.FirstOrDefault(b=>b.Remote&&b.Scope==scope&&b.Id==book.Id);if(existing!=null){existing.Title=book.Title;existing.Author=book.Author;existing.Format=book.Format;existing.OnShelf=book.OnShelf;remoteBooks.Add(existing);}else{store.State.Books.Add(book);remoteBooks.Add(book);}}
    total=result.Total;store.Save();SetBooks(remoteBooks,true);reader.SetStatus("Reader 书架已更新，共 "+total+" 本");
   }catch{
    if(version==fetchVersion){var cached=store.State.Books.Where(b=>b.Remote&&b.Scope==scope&&(b.Title+" "+b.Author).IndexOf(search,StringComparison.CurrentCultureIgnoreCase)>=0).ToList();SetBooks(cached,false);Find<TextBlock>("CountLabel").Text="本机缓存 · "+cached.Count+" 本";}throw;
   }finally{if(version==fetchVersion)Find<Button>("RefreshButton").IsEnabled=true;}
  }
  void SetBooks(List<Book> books,bool paged){
   Find<ListBox>("BookList").ItemsSource=books.Select(b=>new BookRow{Book=b}).ToList();Find<TextBlock>("EmptyBooks").Visibility=books.Count==0?Visibility.Visible:Visibility.Collapsed;
   Find<TextBlock>("CountLabel").Text=paged?"共 "+total+" 本 · 第 "+page+" 页":books.Count+" 本书";
   Find<Button>("PrevBooks").Visibility=paged&&page>1?Visibility.Visible:Visibility.Collapsed;Find<Button>("NextBooks").Visibility=paged&&page*50<total?Visibility.Visible:Visibility.Collapsed;
  }
  void UpdateCurrent(){
   var b=reader.Current;Find<TextBlock>("CurrentTitle").Text=b==null?"还没有打开书籍":b.Title;
   Find<TextBlock>("CurrentChapter").Text=b==null?"选择书籍后，点击开始阅读。":reader.Chapter.Title+"  ·  "+reader.Percentage.ToString("P1")+(reader.Auto?"  ·  自动翻页中":"");
   Find<WrapPanel>("ConflictPanel").Visibility=reader.Conflict==null?Visibility.Collapsed:Visibility.Visible;
   Find<Button>("SyncButton").IsEnabled=b!=null&&b.Remote;Find<Button>("TocButton").IsEnabled=b!=null;
  }
  void LoadConnection(){
   Find<TextBox>("ServerBox").Text=store.State.Account.Server;
   Find<TextBox>("UsernameBox").Text=store.State.Account.Username;
   Find<TextBlock>("ConnectionLabel").Text=ConnectionText();
  }
  void LoadKeys(){var k=store.State.Keys;Find<TextBox>("BossKeyBox").Text=k.Boss;Find<TextBox>("NextKeyBox").Text=k.Next;Find<TextBox>("PrevKeyBox").Text=k.Previous;Find<TextBox>("ManagerKeyBox").Text=k.Manager;Find<TextBlock>("SideBoss").Text=k.Boss+"  隐藏 / 恢复";Find<TextBox>("AutoSecondsBox").Text=store.State.AutoSeconds.ToString();}
  void LoadAppearance(){
   bool previous=loadingSettings;loadingSettings=true;var a=store.State.Appearance;var font=Find<ComboBox>("FontBox");if(font.Items.Count==0)foreach(var f in Fonts.SystemFontFamilies.OrderBy(f=>f.Source))font.Items.Add(f.Source);font.Text=a.Font;
   Find<Slider>("FontSlider").Value=a.FontSize;Find<Slider>("WidthSlider").Value=a.Width;Find<Slider>("HeightSlider").Value=a.Height;Find<Slider>("OpacitySlider").Value=a.Opacity*100;Find<Slider>("BgSlider").Value=a.BackgroundOpacity*100;
   Find<TextBox>("TextColorBox").Text=a.TextColor;Find<TextBox>("BgColorBox").Text=a.Background;Find<CheckBox>("TopCheck").IsChecked=a.Topmost;Find<CheckBox>("ProgressCheck").IsChecked=a.Progress;
   Preview();loadingSettings=previous;
  }
  void Preview(){var a=store.State.Appearance;Find<TextBlock>("FontValue").Text=a.FontSize+" px";Find<TextBlock>("WidthValue").Text=Math.Round(a.Width)+" px";Find<TextBlock>("HeightValue").Text=a.Height+" px";Find<TextBlock>("OpacityValue").Text=Math.Round(a.Opacity*100)+"%";Find<TextBlock>("BgValue").Text=Math.Round(a.BackgroundOpacity*100)+"%";var text=Find<TextBlock>("PreviewText");text.FontFamily=new FontFamily(a.Font);text.FontSize=a.FontSize;text.Foreground=Strip.ColorBrush(a.TextColor,1);Find<Border>("PreviewBorder").Background=Strip.BackgroundBrush(a);Find<Border>("PreviewBorder").Opacity=a.Opacity;}
  static bool ValidColor(string value){return System.Text.RegularExpressions.Regex.IsMatch(value??"",@"^#[0-9a-fA-F]{6}$");}
  void AppearanceChanged(){
   if(loadingSettings)return;var a=store.State.Appearance;string text=Find<TextBox>("TextColorBox").Text,bg=Find<TextBox>("BgColorBox").Text;
   if(!ValidColor(text)||!ValidColor(bg)){reader.SetStatus("颜色请输入 #RRGGBB 格式。");return;}
   a.Font=Find<ComboBox>("FontBox").Text;a.FontSize=Find<Slider>("FontSlider").Value;a.Width=Find<Slider>("WidthSlider").Value;a.Height=Find<Slider>("HeightSlider").Value;a.Opacity=Find<Slider>("OpacitySlider").Value/100;a.BackgroundOpacity=Find<Slider>("BgSlider").Value/100;a.TextColor=text;a.Background=bg;a.Topmost=Find<CheckBox>("TopCheck").IsChecked==true;a.Progress=Find<CheckBox>("ProgressCheck").IsChecked==true;
   strip.Apply();reader.Reflow();Preview();dirtySettings=true;settingsAt=Store.Now;
  }
  void PickColor(string name){using(var dialog=new Forms.ColorDialog()){dialog.FullOpen=true;dialog.Color=System.Drawing.ColorTranslator.FromHtml(Find<TextBox>(name).Text);if(dialog.ShowDialog()==Forms.DialogResult.OK){Find<TextBox>(name).Text=System.Drawing.ColorTranslator.ToHtml(dialog.Color);AppearanceChanged();}}}
  void Preset(string text,string bg,double opacity){Find<TextBox>("TextColorBox").Text=text;Find<TextBox>("BgColorBox").Text=bg;Find<Slider>("BgSlider").Value=opacity*100;AppearanceChanged();}
  void ShowToc(){
   if(reader.Current==null)return;if(tocWindow!=null){tocWindow.Close();tocWindow=null;}
   var book=reader.Current;var list=new ListBox{Margin=new Thickness(18,10,18,18),FontSize=14,DisplayMemberPath="Title"};
   // Chapter is a field-based persistence model; display via simple labels.
   list.DisplayMemberPath="";list.ItemsSource=book.Chapters;list.SelectedIndex=book.Position.Chapter;
   var filter=new TextBox{Margin=new Thickness(18,18,18,0),Padding=new Thickness(10),ToolTip="搜索章节"};
   var root=new DockPanel();DockPanel.SetDock(filter,Dock.Top);root.Children.Add(filter);root.Children.Add(list);
   var win=new Window{Title=book.Title+" · 章节",Width=480,Height=560,Content=root,FontFamily=new FontFamily("Microsoft YaHei UI"),WindowStartupLocation=WindowStartupLocation.CenterScreen,Background=Strip.ColorBrush("#F5F6F2",1),Topmost=true};tocWindow=win;
   filter.TextChanged+=(s,e)=>list.ItemsSource=book.Chapters.Where(c=>c.Title.IndexOf(filter.Text,StringComparison.CurrentCultureIgnoreCase)>=0).ToList();
   Action choose=async()=>await Run(async()=>{var selected=list.SelectedItem as Chapter;if(selected!=null&&reader.Current==book){await reader.Jump(book.Chapters.IndexOf(selected),0);win.Close();strip.Show();}});
   list.MouseDoubleClick+=(s,e)=>choose();list.KeyDown+=(s,e)=>{if(e.Key==Key.Enter)choose();};win.Closed+=(s,e)=>{if(tocWindow==win)tocWindow=null;};win.Show();list.ScrollIntoView(list.SelectedItem);
  }
  async Task Quit(){
   if(quitting)return;quitting=true;timer.Stop();reader.Pause();reader.Save();main.IsEnabled=false;
   try{await reader.Flush();}finally{reader.Save();hotkeys.Dispose();tray.Visible=false;tray.Dispose();api.Dispose();app.Shutdown();}
  }
  public async Task Smoke(){
   var checks=new List<string>();Action<bool,string> check=(ok,name)=>{if(!ok)throw new Exception("UI check failed: "+name);checks.Add(name);};
   try{
    await Open(store.Demo());View("Books");RefreshLocal();main.Show();main.UpdateLayout();
    check(Find<ListBox>("BookList").Items.Count==1,"book card data binding");
    check(strip.IsVisible&&strip.ShowInTaskbar==false&&strip.Topmost,"floating strip window flags");
    check(reader.Line.Contains("山间"),"first line rendered");
    int initial=reader.Current.Position.Offset;await reader.Turn(1);check(reader.Current.Position.Offset>initial,"next line advances");await reader.Turn(-1);check(reader.Current.Position.Offset==initial,"previous line restores source offset");
    await reader.Turn(1);int position=reader.Current.Position.Offset;
    Find<Button>("NavAppearance").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(Find<FrameworkElement>("AppearanceView").Visibility==Visibility.Visible&&Find<FrameworkElement>("BooksView").Visibility==Visibility.Collapsed,"navigation switches panels");
    Find<Slider>("FontSlider").Value=24;Find<Slider>("WidthSlider").Value=610;check(reader.Current.Position.Offset==position&&strip.Width==610,"font and width controls retain source position");
    Find<Slider>("OpacitySlider").Value=70;Find<Slider>("BgSlider").Value=35;check(Math.Abs(strip.Opacity-0.7)<0.001&&Math.Abs(store.State.Appearance.BackgroundOpacity-0.35)<0.001,"opacity controls update native window");
    Find<Button>("PresetPaper").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(store.State.Appearance.Background=="#F5EEDB","color preset applies");
    Find<Button>("PresetDark").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    store.State.Appearance.Width=820;store.State.Appearance.FontSize=18;store.State.Appearance.Opacity=0.92;store.State.Appearance.BackgroundOpacity=0.8;LoadAppearance();strip.Apply();reader.Reflow();
    ShowToc();check(tocWindow!=null&&tocWindow.IsVisible,"chapter window opens");
    BossToggle();check(!main.IsVisible&&!strip.IsVisible&&!tocWindow.IsVisible&&!reader.Auto,"boss key hides all reading windows");
    BossToggle();check(strip.IsVisible&&!main.IsVisible,"boss key restores strip only");
    tocWindow.Close();ShowMain();main.Close();check(!main.IsVisible&&strip.IsVisible,"close console keeps reading active");
    reader.Save();var reloaded=new Store(store.Root);check(reloaded.State.Books[0].Position.Offset==reader.Current.Position.Offset,"reading offset persists across restarts");
    ShowMain();View("Books");main.UpdateLayout();SaveVisual(main,Path.Combine(store.Root,"main.png"));strip.UpdateLayout();SaveVisual(strip,Path.Combine(store.Root,"strip.png"));
    Find<Button>("NavAppearance").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));main.UpdateLayout();SaveVisual(main,Path.Combine(store.Root,"appearance.png"));
    Store.AtomicWrite(Path.Combine(store.Root,"ui-smoke.json"),Json.Stringify(new{passed=true,checks=checks}));
   }catch(Exception ex){Store.AtomicWrite(Path.Combine(store.Root,"ui-smoke.json"),Json.Stringify(new{passed=false,error=ex.ToString(),checks=checks}));Environment.ExitCode=1;}
   finally{quitting=true;timer.Stop();hotkeys.Dispose();tray.Visible=false;tray.Dispose();api.Dispose();app.Shutdown();}
  }
  static void SaveVisual(Window window,string path){var visual=window.Content as FrameworkElement;var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);}
 }
 public static class Program {
  static Mutex mutex;
  [STAThread]public static void Main(string[] args){
   try{
    string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data");for(int i=0;i<args.Length-1;i++)if(args[i]=="--data-dir")root=Path.GetFullPath(args[i+1]);
    bool created;mutex=new Mutex(true,"Local\\OneLineReader-"+Store.Hash(root).Substring(0,16),out created);if(!created){MessageBox.Show("一行 Reader 已在运行，请从系统托盘打开。","一行 Reader");return;}
    var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
    app.DispatcherUnhandledException+=(s,e)=>{e.Handled=true;MessageBox.Show(e.Exception.Message,"一行 Reader",MessageBoxButton.OK,MessageBoxImage.Information);};
    bool smoke=args.Contains("--ui-smoke");var desktop=new DesktopApp(app,root,smoke);if(smoke)app.Dispatcher.BeginInvoke(new Action(async()=>await desktop.Smoke()));app.Run();GC.KeepAlive(desktop);mutex.ReleaseMutex();
   }catch(Exception ex){MessageBox.Show("无法启动："+ex.Message,"一行 Reader",MessageBoxButton.OK,MessageBoxImage.Error);}
  }
 }
}
