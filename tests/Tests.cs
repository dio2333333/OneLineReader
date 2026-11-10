using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneLine {
 public class FakeReader:HttpMessageHandler {
  public List<string> Calls=new List<string>();public int Refreshes,Puts;public bool UnauthorizedOnce,FailPut,RejectPut;public string LastBody="";
  public RemotePosition Progress=new RemotePosition{Locator="r1:0.0000:chapter:0:stable",UpdatedAt=100,ChapterTitle="第一章"};
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken token){
   string path=req.RequestUri.AbsolutePath,query=req.RequestUri.Query,body=req.Content==null?"":await req.Content.ReadAsStringAsync();Calls.Add(req.Method+" "+path+query);
   object result=null;int status=200;
   if(path.EndsWith("/instance"))result=new{name="Reader test",apiVersion=1};
   else if(path.EndsWith("/auth/login")) { var input=Json.Parse(body);if(Json.Str(input,"password")!="test-password")return Response(401,new{error=new{message="bad credentials"}});result=new{session=new{user=new{id="user-A",username="tester"},accessToken="access-old",refreshToken="refresh-old",accessTokenExpiresAt=Store.Now+3600000}}; }
   else if(path.EndsWith("/auth/refresh")){Refreshes++;Check(Json.Str(Json.Parse(body),"refreshToken")=="refresh-old","refresh uses current rotation token");result=new{user=new{id="user-A",username="tester"},accessToken="access-new",refreshToken="refresh-new",accessTokenExpiresAt=Store.Now+3600000};}
   else {
    string access=req.Headers.Authorization==null?"":req.Headers.Authorization.Parameter;
    if(access!="access-old"&&access!="access-new")return Response(401,new{error=new{message="unauthorized"}});
    if(UnauthorizedOnce&&path.EndsWith("/books")){UnauthorizedOnce=false;return Response(401,new{error=new{message="expired"}});}
    if(path.EndsWith("/books"))result=new{items=new[]{new{id="book/id",title="测试小说",author="作者",format="txt"}},total=51,page=2,pageSize=50};
    else if(path.EndsWith("/manifest"))result=new{content=new{kind="text",items=new object[0]}};
    else if(path.EndsWith("/items")) {Check(query.Length==0,"omit group for complete chapter structure");result=new{kind="text",items=new[]{new{href="chapter:0:stable",resourceRef="chapter-resource:revision:secret",title="第一章",mediaType="text/html",kind="chapter"},new{href="chapter:1:stable",resourceRef="chapter-resource:revision:next",title="第二章",mediaType="text/plain",kind="chapter"}}};}
    else if(path.EndsWith("/library/browse/shelf")){Check(Json.Str(Json.Parse(body),"action")=="add","library join uses add action");result=new{changed=1,failed=new object[0]};}
    else if(path.EndsWith("/assets")){Check(query.Contains("chapter-resource%3Arevision"),"resourceRef used instead of locator id");return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<html><head><style>body{color:red}</style></head><body><script>fetch('http://evil')</script><h1>第一章</h1><p>春天来了&amp;山间的信。"+new string('山',250)+"</p></body></html>",Encoding.UTF8,"text/html")};}
    else if(path.Contains("/sync/progress/")) {
     if(req.Method.Method=="PUT") {Puts++;LastBody=body;if(FailPut)return Response(503,new{error=new{message="offline"}});if(!RejectPut){var p=Json.Parse(body);Progress=new RemotePosition{Locator=Json.Str(p,"locator"),Percentage=Json.Num(p,"percentage"),UpdatedAt=(long)Json.Num(p,"updatedAt"),ChapterTitle=Json.Str(p,"chapterTitle"),Device=Json.Str(p,"device")};}}
     result=new{progress=new{locator=Progress.Locator,updatedAt=Progress.UpdatedAt,percentage=Progress.Percentage,chapterTitle=Progress.ChapterTitle,device=Progress.Device}};
    }else if(path.EndsWith("/auth/logout"))result=new{ok=true};else return Response(404,new{error=new{message="unexpected route"}});
   }
   return Response(status,result);
  }
  static HttpResponseMessage Response(int status,object body){return new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(Json.Stringify(body),Encoding.UTF8,"application/json")};}
  static void Check(bool value,string msg){if(!value)throw new Exception("Fake contract failed: "+msg);}
 }
 public static class Tests {
  static int passed;static string root;
  static void Check(bool b,string message){if(!b)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
  [STAThread]public static int Main(string[] args){
   root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-results",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
   try{if(args.Length==2&&args[0]=="--probe"){using(var api=new ReaderClient(new Store(root)))Console.WriteLine("LIVE PROBE: "+api.Probe(args[1]).GetAwaiter().GetResult());return 0;}TextTests();StorageTests();ApiTests().GetAwaiter().GetResult();Console.WriteLine("ALL PASSED: "+passed);return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  }
  static void TextTests(){
   string sample="序章 一封信\n春天来了。\n第一章 桥边\n这里是第一章。\n第二章 归来\n这是第二章。";
   var chapters=TextBook.Split(sample);Check(chapters.Count==3,"Chinese chapter splitting");Check(chapters[1].Title=="第一章 桥边","chapter heading preserved");
   Check(TextBook.Decode(Encoding.GetEncoding("GB18030").GetBytes(sample))==sample,"GB18030 decoded");Check(TextBook.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(sample)).ToArray())==sample,"UTF16 BOM decoded");
   Check(TextBook.Clean("<head><style>bad</style></head><script>bad()</script><p>中文&amp;内容</p>",true)=="中文&内容","HTML strips executable and style content");
   string content=string.Concat(Enumerable.Repeat("中文😀e\u0301与English 123，。",100));var pager=new LinePager(content,"Microsoft YaHei UI",18,220);int offset=0;var joined=new StringBuilder();int loops=0;
   while(offset<pager.Length){int end=pager.End(offset);if(end<=offset)throw new Exception("pagination did not advance");if(end<content.Length&&char.IsLowSurrogate(content[end]))throw new Exception("surrogate pair split");joined.Append(pager.Line(offset));offset=end;if(++loops>1000)throw new Exception("pagination loop");}
   Check(joined.ToString()==content,"all Unicode text survives paging exactly");
   Check(new LinePager(content,"Microsoft YaHei UI",18,500).End(0)>pager.End(0),"actual font width drives pagination");
   Check(Locator.Format("xhtml:path:with:colons",0.123456)=="r1:0.1235:xhtml:path:with:colons","native r1 locator serialized");string id;double fraction;Check(Locator.Parse("r1:0.5000:chapter:7:stable",out id,out fraction)&&id=="chapter:7:stable"&&fraction==0.5,"locator keeps all section colons");Check(!Locator.Parse("epubcfi(/6/4)",out id,out fraction),"unsupported locator rejected");
   var store=new Store(Path.Combine(root,"imports"));string txt=Path.Combine(root,"novel.txt");File.WriteAllBytes(txt,Encoding.GetEncoding("GB18030").GetBytes(sample));var b=TextBook.Import(txt,store);Check(File.Exists(b.LocalPath)&&b.Chapters.Count==3,"TXT imported and original copied");Check(store.State.Books.Count==1&&TextBook.Import(txt,store)==b,"duplicate import reuses book");
   string epub=Path.Combine(root,"sample.epub");using(var z=ZipFile.Open(epub,ZipArchiveMode.Create)){
    Write(z,"META-INF/container.xml","<container><rootfiles><rootfile full-path='OPS/book.opf'/></rootfiles></container>");Write(z,"OPS/book.opf","<package><metadata><title>测试 EPUB</title><creator>测试作者</creator></metadata><manifest><item id='a' href='ch1.xhtml'/><item id='b' href='sub/ch2.xhtml'/></manifest><spine><itemref idref='a'/><itemref idref='b'/></spine></package>");Write(z,"OPS/ch1.xhtml","<h1>第一章 信</h1><p>正文。</p>");Write(z,"OPS/sub/ch2.xhtml","<h1>第二章 风</h1><p>山风。</p>");}
   var e=TextBook.Import(epub,store);Check(e.Title=="测试 EPUB"&&e.Chapters.Count==2&&e.Chapters[1].Text.Contains("山风"),"EPUB spine order and relative paths extracted");
  }
  static void Write(ZipArchive z,string path,string value){using(var w=new StreamWriter(z.CreateEntry(path).Open(),new UTF8Encoding(false)))w.Write(value);}
  static void StorageTests(){
   var store=new Store(Path.Combine(root,"storage"));store.State.LastBook="first";store.Save();store.State.LastBook="second";store.Save();File.WriteAllText(Path.Combine(store.Root,"state.json"),"broken");var recovered=new Store(store.Root);Check(recovered.State.LastBook=="first"&&recovered.Warning.Length>0,"atomic backup recovers corrupted configuration");
   string cipher=Store.Protect("secret-value");Check(!cipher.Contains("secret-value")&&Store.Unprotect(cipher)=="secret-value","DPAPI session round-trip");
   Check(ReaderClient.NormalizeServer("https://example.com/reader/api/v1/")=="https://example.com/reader","reverse proxy base normalized");
   bool rejected=false;try{ReaderClient.NormalizeServer("https://user:pass@example.com/?secret=yes");}catch{rejected=true;}Check(rejected,"credential-bearing service URLs rejected");
  }
  static async Task ApiTests(){
   var store=new Store(Path.Combine(root,"api"));var fake=new FakeReader();using(var api=new ReaderClient(store,fake)){
    await api.Login("http://reader.test/base/","tester","test-password");Check(api.SignedIn&&store.State.Account.UserId=="user-A","login session wrapper accepted");
    Check(!File.ReadAllText(Path.Combine(store.Root,"state.json")).Contains("test-password"),"password not persisted");
    fake.UnauthorizedOnce=true;var shelf=await api.Shelf("library","中文",2);Check(shelf.Total==51&&shelf.Books.Count==1,"paged online library read");Check(fake.Refreshes==1&&Store.Unprotect(store.State.Account.Refresh)=="refresh-new","401 refresh rotates and persists session");
    Check(fake.Calls.Any(c=>c.Contains("scope=library")&&c.Contains("page=2")&&c.Contains("%E4%B8%AD")),"scope pagination and Unicode query encoded");
    var b=shelf.Books[0];b.OnShelf=false;store.State.Books.Add(b);await api.LoadChapters(b);Check(b.Chapters.Count==2&&b.Chapters[0].Id=="chapter:0:stable","full server chapter identities preserved");Check(b.OnShelf&&fake.Calls.Any(c=>c.Contains("/library/browse/shelf")),"opening library book adds it to personal shelf");
    string text=await api.ChapterText(b,b.Chapters[0]);Check(text.Contains("春天来了&")&&!text.Contains("fetch("),"remote HTML extracted as safe text");int requests=fake.Calls.Count;Check(await api.ChapterText(b,b.Chapters[0])==text&&fake.Calls.Count==requests,"loaded chapters reused offline");
    var controller=new ReadingController(store,api);await controller.Open(b);Check(controller.Current.Position.BaseRemote==100,"opening adopts cloud progress");await controller.Turn(1);Check(b.Position.Dirty&&b.Position.Offset>0,"page turn records pending position");
    fake.FailPut=true;await controller.Sync(false);Check(b.Position.Dirty&&controller.Status.Contains("未同步"),"failed upload retains pending progress");fake.FailPut=false;await controller.Sync(true);Check(!b.Position.Dirty&&fake.LastBody.Contains("r1:")&&fake.LastBody.Contains("chapter:0:stable"),"successful upload uses browser-compatible locator");
    long now=Store.Now;fake.Progress=new RemotePosition{Locator="r1:0.5000:chapter:0:stable",UpdatedAt=now+100,Device="web"};await controller.Turn(1);await controller.Sync(false);Check(controller.Conflict!=null&&b.Position.Dirty,"concurrent device progress surfaces conflict");
    await controller.ResolveConflict(true);Check(controller.Conflict==null&&!b.Position.Dirty&&controller.Fraction>0.45,"cloud conflict resolution navigates within chapter");
    await controller.Turn(1);fake.Progress=new RemotePosition{Locator="r1:0.1000:chapter:1:stable",UpdatedAt=Store.Now+200};await controller.Sync(false);Check(controller.Conflict!=null,"second remote conflict detected");await controller.ResolveConflict(false);Check(controller.Conflict==null&&!b.Position.Dirty,"local conflict choice explicitly overrides with fresh timestamp");
    long previous=b.Position.UpdatedAt;await controller.Turn(1);Check(b.Position.UpdatedAt>previous,"progress clock remains monotonic after future cloud timestamp");
    string oldScope=store.State.Account.Scope;store.State.Account.UserId="user-B";bool isolated=false;try{await api.GetProgress(b);}catch{isolated=true;}Check(isolated,"cross-account book requests blocked");store.State.Account.UserId="user-A";
    Check(store.State.Account.Scope==oldScope,"account scope deterministic");await api.Logout();Check(!api.SignedIn&&store.State.Account.Refresh=="","logout clears saved credentials");
   }
  }
 }
}
