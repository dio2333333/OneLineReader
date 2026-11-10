using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace OneLine {
 public class LinePager {
  readonly string text;readonly Typeface font;readonly double size,width;readonly int[] breaks;
  public LinePager(string value,string family,double fontSize,double availableWidth){text=(value??"").Replace('\n',' ').Replace('\r',' ').Replace('\t',' ');font=new Typeface(family);size=fontSize;width=Math.Max(20,availableWidth);breaks=StringInfo.ParseCombiningCharacters(text);}
  public int Length{get{return text.Length;}}
  public int Safe(int offset){offset=Math.Max(0,Math.Min(offset,text.Length));if(offset==text.Length)return offset;int i=Array.BinarySearch(breaks,offset);return i>=0?offset:breaks[Math.Max(0,(~i)-1)];}
  public int End(int start){
   start=Safe(start);if(start>=text.Length)return text.Length;
   int baseIndex=Array.BinarySearch(breaks,start);if(baseIndex<0)baseIndex=~baseIndex;
   int lo=baseIndex+1,hi=Math.Min(breaks.Length,baseIndex+512),best=lo;
   while(lo<=hi){int m=(lo+hi)/2,end=m<breaks.Length?breaks[m]:text.Length;
    var ft=new FormattedText(text.Substring(start,end-start),CultureInfo.CurrentCulture,FlowDirection.LeftToRight,font,size,Brushes.Black,1.0);
    if(ft.WidthIncludingTrailingWhitespace<=width){best=m;lo=m+1;}else hi=m-1;
   }
   return best<breaks.Length?breaks[best]:text.Length;
  }
  public string Line(int start){start=Safe(start);return text.Substring(start,End(start)-start);}
  public int Previous(int start){
   start=Safe(start);int n=Math.Max(0,start-1024),previous=n;
   n=Safe(n);while(n<start){previous=n;int next=End(n);if(next>=start||next<=n)break;n=next;}return previous;
  }
 }
 public class ReadingController {
  public readonly Store Store;public readonly ReaderClient Api;
  public Book Current;
  public RemotePosition Conflict;
  public bool Busy,Auto;
  public string Status="本机进度自动保存";
  public event Action Changed,StatusChanged;
  public LinePager Pager;
  readonly Stack<int> history=new Stack<int>();
  bool syncing,flushRequested,heartbeatPending,retryPending;
  long changedAt,lastEditAt,lastAuto,retryAt,nextHeartbeatAt,lastLocalSaveAt;
  int syncFailures;
  Task activeSync;
  const int UploadIdleMs=30000,HeartbeatIntervalMs=5*60*1000;
  public ReadingController(Store s,ReaderClient api){Store=s;Api=api;nextHeartbeatAt=Store.Now+HeartbeatIntervalMs;lastLocalSaveAt=Store.Now;}
  public Chapter Chapter{get{return Current!=null&&Current.Chapters.Count>0?Current.Chapters[Math.Max(0,Math.Min(Current.Position.Chapter,Current.Chapters.Count-1))]:null;}}
  public string Line{get{return Pager==null?"从书架选择一本书，开始一行阅读。":Pager.Line(Current.Position.Offset);}}
  public double Fraction{get{return Pager==null||Pager.Length==0?0:(double)Current.Position.Offset/Pager.Length;}}
  public double Percentage{get{return Current==null||Current.Chapters.Count==0?0:(Current.Position.Chapter+Fraction)/Current.Chapters.Count;}}
  void Notify(){if(Changed!=null)Changed();}
  public void SetStatus(string text){Status=text;if(StatusChanged!=null)StatusChanged();}
  public void Reflow(){if(Chapter==null)return;var a=Store.State.Appearance;Pager=new LinePager(Chapter.Text??"",a.Font,a.FontSize,a.Width-42);Current.Position.Offset=Pager.Safe(Current.Position.Offset);history.Clear();Notify();}
  public async Task Open(Book book){
   if(Busy)return;Busy=true;
   try{
    if(Current!=null)await Flush();
    if(book.Remote){Api.RequireScope(book);if(book.Chapters.Count==0)await Api.LoadChapters(book);}
    if(book.Chapters.Count==0)throw new Exception("这本书没有文字内容。");
    int chapter=Math.Max(0,Math.Min(book.Position.Chapter,book.Chapters.Count-1));
    if(book.Remote)await Api.ChapterText(book,book.Chapters[chapter]);
    Current=book;Current.Position.Chapter=chapter;Conflict=null;Auto=false;Store.State.LastBook=book.Scope+"|"+book.Id;
    Reflow();Store.Save();
    if(book.Remote)await Sync(false,true);else SetStatus("本地阅读 · 进度自动保存");
    Notify();
   }finally{Busy=false;}
  }
  void Touch(){
   if(Current==null)return;
   Current.Position.UpdatedAt=Math.Max(Store.Now,Math.Max(Current.Position.UpdatedAt,Current.Position.BaseRemote)+1);Current.Position.Dirty=Current.Remote;changedAt=lastEditAt=Store.Now;
   if(Current.Remote&&Conflict==null)SetStatus(syncing?"正在同步；新的本机位置已排队":"本机位置待同步 · 停翻 30 秒上传，每 5 分钟保底握手");
   Notify();
  }
  public async Task Turn(int delta){
   if(Busy||Current==null||Pager==null)return;
   int offset=Current.Position.Offset;
   if(delta>0){int end=Pager.End(offset);if(end<Pager.Length){history.Push(offset);Current.Position.Offset=end;Touch();return;}if(Current.Position.Chapter+1<Current.Chapters.Count)await Jump(Current.Position.Chapter+1,0);else{Auto=false;SetStatus("已到全书末尾");}}
   else{if(offset>0){Current.Position.Offset=history.Count>0?history.Pop():Pager.Previous(offset);Touch();return;}if(Current.Position.Chapter>0)await Jump(Current.Position.Chapter-1,1);else SetStatus("已到全书开头");}
  }
  public async Task Jump(int index,double fraction){
   if(Busy||Current==null)return;Busy=true;
   try{await Navigate(index,fraction);Touch();}finally{Busy=false;}
  }
  async Task Navigate(int index,double fraction){
   var b=Current;if(b==null)return;index=Math.Max(0,Math.Min(index,b.Chapters.Count-1));var c=b.Chapters[index];
   if(b.Remote)await Api.ChapterText(b,c);
   if(Current!=b)return;b.Position.Chapter=index;b.Position.Offset=(int)((c.Text??"").Length*Store.Clamp(fraction,0,1));
   if(fraction>=1&&b.Position.Offset>0)b.Position.Offset=Math.Max(0,b.Position.Offset-1);
   Reflow();if(fraction>=1&&Pager!=null)b.Position.Offset=Pager.Previous(Pager.Length);Notify();
  }
  RemotePosition Snapshot(Book b){var c=Chapter;return new RemotePosition{Locator=Locator.Format(c.Id,Fraction),ChapterTitle=c.Title,Percentage=Percentage,UpdatedAt=b.Position.UpdatedAt};}
  async Task<bool> ApplyRemote(RemotePosition p){
   string id;double offset;if(!Locator.Parse(p.Locator,out id,out offset)){SetStatus("云端位置格式不支持，已保留本机位置。");return false;}
   int index=Current.Chapters.FindIndex(c=>c.Id==id||c.Id.Replace("xhtml:","")==id.Replace("xhtml:",""));
   if(index<0){SetStatus("云端章节未找到，已保留本机位置。请刷新章节或选择目录。");return false;}
   await Navigate(index,offset);var pos=Current.Position;pos.UpdatedAt=p.UpdatedAt;pos.SyncedAt=p.UpdatedAt;pos.BaseRemote=p.UpdatedAt;pos.Dirty=false;Store.Save();return true;
  }
  public Task Sync(bool manual,bool opening=false){
   if(Current==null||!Current.Remote)return Task.FromResult(0);
   if(syncing){flushRequested=true;return activeSync??Task.FromResult(0);}
   activeSync=SyncCore(manual,opening);return activeSync;
  }
  async Task SyncCore(bool manual,bool opening){
   if(Conflict!=null){SetStatus("进度冲突：请在书架中选择保留本机或采用云端。");return;}
   syncing=true;flushRequested=false;retryPending=false;var b=Current;
   try{
    Store.Save();SetStatus("正在同步 Reader 阅读进度…");
    Api.RequireScope(b);var remote=await Api.GetProgress(b);if(Current!=b)return;
    if(remote!=null&&remote.UpdatedAt>b.Position.BaseRemote){
     if(b.Position.Dirty&&remote.Locator!=Snapshot(b).Locator){Conflict=remote;Auto=false;SetStatus("另一台设备更新了进度，请选择保留本机或采用云端。");Notify();return;}
     if(!b.Position.Dirty&&(opening||remote.UpdatedAt>b.Position.UpdatedAt)){
      // Turn input is blocked only while applying a cloud position.
      bool wasBusy=Busy;Busy=true;try{if(await ApplyRemote(remote))SetStatus("已采用云端阅读位置");}finally{Busy=wasBusy;}
      syncFailures=0;retryAt=0;
      return;
     }
     b.Position.BaseRemote=remote.UpdatedAt;
    }
    if(b.Position.Dirty){
     var snapshot=Snapshot(b);var result=await Api.PutProgress(b,snapshot);
     if(Current!=b)return;
     b.Position.BaseRemote=Math.Max(b.Position.BaseRemote,result.UpdatedAt);
     if(result.Locator!=snapshot.Locator&&result.UpdatedAt>=snapshot.UpdatedAt){Conflict=result;SetStatus("上传期间云端进度已变化，请选择要保留的位置。");Notify();return;}
     if(result.UpdatedAt<snapshot.UpdatedAt)throw new Exception("服务尚未确认最新位置，稍后会重试。");
     b.Position.SyncedAt=snapshot.UpdatedAt;if(b.Position.UpdatedAt==snapshot.UpdatedAt)b.Position.Dirty=false;
     Store.Save();SetStatus(b.Position.Dirty?"较新的本机位置待同步":"已同步 · "+DateTime.Now.ToString("HH:mm:ss"));
    }else SetStatus("已与 Reader 对齐 · "+DateTime.Now.ToString("HH:mm:ss"));
    syncFailures=0;retryAt=0;
   }catch(Exception ex){ScheduleRetry(ex,b.Position.Dirty);if(manual)throw;}
   finally{syncing=false;}
  }
  // Never await the network from the periodic persistence loop or the boss key.
  // Local saves continue every tick even when a Reader request is timing out.
  async void StartBackgroundSync(){try{await Sync(false);}catch(Exception ex){SetStatus("同步失败，本机位置保留："+ex.Message);}}
  void ScheduleRetry(Exception ex,bool dirty){
   retryPending=true;syncFailures++;int retrySeconds=Math.Min(15,3*(1<<Math.Min(syncFailures-1,3)));retryAt=Store.Now+retrySeconds*1000;
   SetStatus((dirty?"本机已保存，云端未同步：":"同步失败：")+ex.Message+"（"+retrySeconds+" 秒后重试）");
  }
  async Task HeartbeatCore(){
   syncing=true;retryPending=false;var book=Current;
   try{
    if(book!=null&&book.Remote&&Conflict!=null){
     // Refresh the conflicting remote record without silently choosing a side.
     var remote=await Api.GetProgress(book);
     if(Current==book&&remote!=null&&Conflict!=null&&remote.UpdatedAt>Conflict.UpdatedAt){Conflict=remote;Notify();}
     SetStatus("Reader 握手成功 · 进度冲突仍待处理");
    }else{
     // No online book is open: validate the session rather than fetching a book
     // or manufacturing an unchanged progress write merely to keep contact.
     await Api.Heartbeat();SetStatus("Reader 握手成功 · "+DateTime.Now.ToString("HH:mm:ss"));
    }
    syncFailures=0;retryAt=0;
   }catch(Exception ex){ScheduleRetry(ex,book!=null&&book.Position.Dirty);}
   finally{syncing=false;}
  }
  void StartScheduledContact(){
   if(Current!=null&&Current.Remote&&Conflict==null){StartBackgroundSync();return;}
   activeSync=HeartbeatCore();ObserveContact(activeSync);
  }
  async void ObserveContact(Task contact){try{await contact;}catch(Exception ex){SetStatus("Reader 握手失败："+ex.Message);}}
  public void RequestSync(bool checkRemote=false){
   if(Current==null)return;
   try{Store.Save();}catch(Exception ex){SetStatus("本机保存失败："+ex.Message);return;}
   if(!Current.Remote||Conflict!=null)return;
   if(!Current.Position.Dirty&&!checkRemote)return;
   flushRequested=true;
   if(!syncing)StartBackgroundSync();
  }
  public async Task Flush(){
   var book=Current;var pending=activeSync;
   Store.Save();
   if(syncing&&pending!=null){try{await pending;}catch{}}
   if(book!=null&&Current==book&&book.Remote&&book.Position.Dirty)await Sync(false);
  }
  public async Task ResolveConflict(bool useRemote){
   var remote=Conflict;if(remote==null||Current==null)return;
   if(useRemote){Busy=true;try{if(await ApplyRemote(remote)){Conflict=null;SetStatus("已采用云端位置");}}finally{Busy=false;}}
   else{Current.Position.BaseRemote=remote.UpdatedAt;Current.Position.UpdatedAt=Math.Max(Store.Now,remote.UpdatedAt+1);Current.Position.Dirty=true;Conflict=null;Store.Save();await Sync(true);}
   Notify();
  }
  public void Save(){Store.Save();}
  public void Pause(){Auto=false;Notify();}
  public async Task Tick(bool visible){
   long now=Store.Now;
   if(changedAt>0&&now-lastLocalSaveAt>=500){Store.Save();changedAt=0;lastLocalSaveAt=now;}
   if(Auto&&visible&&!Busy&&Conflict==null&&now-lastAuto>=Store.State.AutoSeconds*1000){lastAuto=now;await Turn(1);}
   if(now>=nextHeartbeatAt){
    heartbeatPending=true;
    // This fixed schedule is independent of page turns and successful uploads.
    // After sleep, collapse missed slots to one contact rather than a burst.
    nextHeartbeatAt+=((now-nextHeartbeatAt)/HeartbeatIntervalMs+1)*HeartbeatIntervalMs;
   }
   if(Api.SignedIn&&!Busy&&!syncing&&now>=retryAt){
    if(heartbeatPending||retryPending){heartbeatPending=false;StartScheduledContact();}
    else if(Current!=null&&Current.Remote&&Conflict==null){
     bool uploadDue=Current.Position.Dirty&&now-lastEditAt>=UploadIdleMs;
     if(flushRequested||uploadDue)StartBackgroundSync();
    }
   }
  }
  public void ToggleAuto(){Auto=!Auto;lastAuto=Store.Now;Notify();}
 }
}
