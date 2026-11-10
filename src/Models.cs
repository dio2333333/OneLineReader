using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Script.Serialization;
using System.IO.Compression;
using System.Xml.Linq;

namespace OneLine {
 public static class Json {
  public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=32*1024*1024, RecursionLimit=100 }; }
  public static string Stringify(object x) { return Serializer().Serialize(x); }
  public static Dictionary<string,object> Obj(object x) { return x as Dictionary<string,object> ?? new Dictionary<string,object>(); }
  public static object Get(object x,string k) { object v; return Obj(x).TryGetValue(k,out v)?v:null; }
  public static string Str(object x,string k) { return Convert.ToString(Get(x,k),CultureInfo.InvariantCulture)??""; }
  public static double Num(object x,string k,double d=0) { double v; return double.TryParse(Str(x,k),NumberStyles.Any,CultureInfo.InvariantCulture,out v)?v:d; }
  public static List<object> List(object x) { var a=x as IEnumerable; return a==null || x is string ? new List<object>() : a.Cast<object>().ToList(); }
  public static object Parse(string s) { return Serializer().DeserializeObject(s); }
 }
 public class Appearance {
  public const double MinimumBackgroundOpacity=0.01;
  public string Font="Microsoft YaHei UI", TextColor="#DCE6DF", Background="#192A24";
  public double FontSize=18, Width=820, Height=48, Opacity=0.92, BackgroundOpacity=0.8, X=80, Y=160;
  public bool Topmost=true, Progress=true;
 }
 public class KeysConfig { public string Boss="Alt+V", Next="Alt+C", Previous="Alt+Z", Manager="Alt+S"; }
 public class Account {
  public string Server="", UserId="", Username="", Access="", Refresh="";
  public long AccessExpires;
  public string Scope { get { return string.IsNullOrEmpty(UserId)?"":Store.Hash(Server.TrimEnd('/').ToLowerInvariant()+"|"+UserId); } }
 }
 public class Position {
  public int Chapter, Offset;
  public long UpdatedAt, SyncedAt, BaseRemote;
  public bool Dirty;
 }
 public class Chapter {
  public string Id="", Ref="", Title="", MediaType="text/plain";
  public string Text; // Null means not cached yet.
  public override string ToString() { return Title; }
 }
 public class Book {
  public string Id="", Title="", Author="", Format="txt", Scope="", LocalPath="";
  public bool OnShelf=true;
  public List<Chapter> Chapters=new List<Chapter>();
  public Position Position=new Position();
  public bool Remote { get { return Scope.Length>0; } }
  public override string ToString() { return Title; }
 }
 public class State {
  public Appearance Appearance=new Appearance();
  public KeysConfig Keys=new KeysConfig();
  public Account Account=new Account();
  public List<Book> Books=new List<Book>();
  public string LastBook="";
  public int AutoSeconds=6;
 }
 public class Store {
  readonly object saveGate=new object();
  public readonly string Root;
  public State State;
  public string Warning="";
  public Store(string root) {
   Root=Path.GetFullPath(root); Directory.CreateDirectory(Root);
   State=Load<State>(Path.Combine(Root,"state.json"));
   if(State==null) { State=Load<State>(Path.Combine(Root,"state.json.bak")); if(State!=null) Warning="配置已从备份恢复。"; }
   if(State==null) State=new State();
   if(State.Appearance==null)State.Appearance=new Appearance();
   if(State.Account==null)State.Account=new Account();
   if(State.Keys==null)State.Keys=new KeysConfig();
   if(State.Books==null)State.Books=new List<Book>();
   ValidateAppearance(State.Appearance);
   foreach(var b in State.Books){ if(b.Position==null)b.Position=new Position(); if(b.Chapters==null)b.Chapters=new List<Chapter>(); }
  }
  public static void ValidateAppearance(Appearance a) {
   a.Width=Clamp(a.Width,240,2400); a.Height=Clamp(a.Height,28,150); a.FontSize=Clamp(a.FontSize,10,64);
   a.Opacity=Clamp(a.Opacity,0.15,1);
   a.BackgroundOpacity=Clamp(a.BackgroundOpacity,Appearance.MinimumBackgroundOpacity,1);
   if(double.IsNaN(a.X)||double.IsInfinity(a.X))a.X=80;if(double.IsNaN(a.Y)||double.IsInfinity(a.Y))a.Y=160;
  }
  public static double Clamp(double v,double a,double b) { return double.IsNaN(v)||double.IsInfinity(v)?a:Math.Max(a,Math.Min(b,v)); }
  public static long Now {get{return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();}}
  public static string Hash(string s) {using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-","").ToLowerInvariant();}
  public static T Load<T>(string path) where T:class { try{return Json.Serializer().Deserialize<T>(File.ReadAllText(path,Encoding.UTF8));}catch{return null;} }
  public static void AtomicWrite(string path,string content) {
   Directory.CreateDirectory(Path.GetDirectoryName(path)); string temp=path+".tmp";
   using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) {var bytes=new UTF8Encoding(false).GetBytes(content);stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
   if(File.Exists(path))File.Replace(temp,path,path+".bak",true);else File.Move(temp,path);
  }
  public void Save() { lock(saveGate) AtomicWrite(Path.Combine(Root,"state.json"),Json.Stringify(State)); }
  public string CachePath(Book b,Chapter c) {return Path.Combine(Root,"cache",Hash(b.Scope+"|"+b.Id),Hash(c.Id+"|"+c.Ref)+".txt");}
  public string ReadCache(Book b,Chapter c) {try {var p=CachePath(b,c);return File.Exists(p)?File.ReadAllText(p,Encoding.UTF8):null;}catch{return null;}}
  public void WriteCache(Book b,Chapter c,string text) {AtomicWrite(CachePath(b,c),text);}
  public static string Protect(string value) { if(string.IsNullOrEmpty(value))return "";return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),null,DataProtectionScope.CurrentUser)); }
  public static string Unprotect(string value) {try{return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser));}catch{return "";} }
  public Book Demo() {
   var existing=State.Books.FirstOrDefault(item=>item.Id=="demo"); if(existing!=null)return existing;
   var b=new Book{Id="demo",Title="山间来信 · 使用示例",Author="一行 Reader",Format="txt"};
   b.Chapters.Add(new Chapter{Id="demo:0",Title="第一章 山间来信",Text="第一章 山间来信\n清晨，山里的邮局收到一封没有寄件人的信。信封上只写着一句话：等雾散了，就去桥边。\n林舟把信收进口袋，沿着石阶向河谷走去。昨夜的雨停了，树梢还挂着细小的水珠。\n这是阅读示例。左键或滚轮向下翻到下一行，方向键也可以。右键打开菜单，拖动阅读条就能调整位置。\n按 Alt+V 隐藏，再按一次恢复。Alt+C 和 Alt+Z 可以在其他软件中翻页。\n桥下传来流水声。那个人还没有来，但远处的云已经慢慢散开。"});
   b.Chapters.Add(new Chapter{Id="demo:1",Title="第二章 雾散之后",Text="第二章 雾散之后\n午后，林舟在桥边找到一只旧木盒。盒子没有上锁，里面放着一张地图。\n你可以导入自己的 TXT 或 EPUB，也可以连接 Reader，继续在线书架里的故事。\n调整字号或阅读条宽度后，程序会从当前字符重新排版，不会跳回开头。\n此刻，山风吹过桥面，故事刚刚开始。"});
   State.Books.Add(b);return b;
  }
 }
 public static class TextBook {
  public static string Decode(byte[] data) {
   if(data.Length>=2&&data[0]==0xff&&data[1]==0xfe)return Encoding.Unicode.GetString(data,2,data.Length-2);
   if(data.Length>=2&&data[0]==0xfe&&data[1]==0xff)return Encoding.BigEndianUnicode.GetString(data,2,data.Length-2);
   try{return new UTF8Encoding(false,true).GetString(data).TrimStart('\ufeff');}catch(DecoderFallbackException){return Encoding.GetEncoding("GB18030").GetString(data);}
  }
  public static string Clean(string text,bool html) {
   text=text??"";
   if(html){ text=Regex.Replace(text,@"<!--[\s\S]*?-->","");text=Regex.Replace(text,@"<(script|style|head|template|noscript)\b[^>]*>[\s\S]*?</\1\s*>","",RegexOptions.IgnoreCase);text=Regex.Replace(text,@"</?(?:br|p|div|h[1-6]|li|section|article|blockquote|tr|hr)\b[^>]*>","\n",RegexOptions.IgnoreCase);text=Regex.Replace(text,@"<[^>]+>","");text=HttpUtility.HtmlDecode(text); }
   return text.Replace("\r\n","\n").Replace('\r','\n').Replace('\0',' ').Trim();
  }
  public static List<Chapter> Split(string text) {
   text=Clean(text,false);
   var matches=Regex.Matches(text,@"(?m)^[\t \u3000]*(?:第[零〇一二三四五六七八九十百千万两0-9０-９]+[章回节卷部篇集][^\n]{0,55}|Chapter\s+\d+[^\n]{0,55}|序章[^\n]{0,30}|楔子[^\n]{0,30})$",RegexOptions.IgnoreCase);
   var chapters=new List<Chapter>();int end=0;
   if(matches.Count>0){
    if(matches[0].Index>0&&text.Substring(0,matches[0].Index).Trim().Length>0)chapters.Add(new Chapter{Title="前言",Text=text.Substring(0,matches[0].Index).Trim()});
    for(int i=0;i<matches.Count;i++){end=i+1<matches.Count?matches[i+1].Index:text.Length;chapters.Add(new Chapter{Title=matches[i].Value.Trim(),Text=text.Substring(matches[i].Index,end-matches[i].Index).Trim()});}
   }else{
    // Bound giant unchaptered TXT files so pagination never measures an entire novel.
    int n=0;while(n<text.Length){end=Math.Min(text.Length,n+24000);if(end<text.Length&&end>n&&char.IsHighSurrogate(text[end-1]))end--; chapters.Add(new Chapter{Title="正文 "+(chapters.Count+1),Text=text.Substring(n,end-n)});n=end;}
   }
   if(chapters.Count==0)chapters.Add(new Chapter{Title="正文",Text="（这一章没有可显示的文字）"});
   for(int i=0;i<chapters.Count;i++)chapters[i].Id="local:"+i;
   return chapters;
  }
  public static Book Import(string path,Store store) {
   var info=new FileInfo(path);if(info.Length>80*1024*1024)throw new Exception("文件超过 80 MB，请先拆分后再导入。");
   string ext=Path.GetExtension(path).ToLowerInvariant();if(ext!=".txt"&&ext!=".epub")throw new Exception("仅支持 TXT 和 EPUB。");
   string id=Store.Hash(path+"|"+info.Length+"|"+info.LastWriteTimeUtc.Ticks);
   var existing=store.State.Books.FirstOrDefault(item=>!item.Remote&&item.Id==id);if(existing!=null)return existing;
   var b=new Book{Id=id,Title=Path.GetFileNameWithoutExtension(path),Format=ext.Substring(1)};
   if(ext==".txt")b.Chapters=Split(Decode(File.ReadAllBytes(path)));else ReadEpub(path,b);
   string dest=Path.Combine(store.Root,"books",id+ext);Directory.CreateDirectory(Path.GetDirectoryName(dest));File.Copy(path,dest,true);b.LocalPath=dest;
   store.State.Books.Add(b);store.Save();return b;
  }
  static string ZipText(ZipArchive zip,string p){var e=zip.GetEntry(p);if(e==null)throw new Exception("EPUB 中缺少 "+p);if(e.Length>16*1024*1024)throw new Exception("EPUB 章节过大。");using(var s=e.Open())using(var r=new StreamReader(s,Encoding.UTF8,true))return r.ReadToEnd();}
  static string Resolve(string opf,string href){return Uri.UnescapeDataString(new Uri(new Uri("https://epub.invalid/"+opf),href).AbsolutePath.TrimStart('/'));}
  static XDocument Xml(string s){ using(var r=System.Xml.XmlReader.Create(new StringReader(s),new System.Xml.XmlReaderSettings{DtdProcessing=System.Xml.DtdProcessing.Ignore,XmlResolver=null}))return XDocument.Load(r); }
  static void ReadEpub(string path,Book b){
   using(var z=ZipFile.OpenRead(path)){
    var container=Xml(ZipText(z,"META-INF/container.xml"));var root=container.Descendants().FirstOrDefault(x=>x.Name.LocalName=="rootfile");if(root==null)throw new Exception("EPUB 缺少内容清单。");
    string opf=(string)root.Attribute("full-path");var pkg=Xml(ZipText(z,opf));var title=pkg.Descendants().FirstOrDefault(x=>x.Name.LocalName=="title");if(title!=null)b.Title=title.Value;
    var author=pkg.Descendants().FirstOrDefault(x=>x.Name.LocalName=="creator");if(author!=null)b.Author=author.Value;
    var items=pkg.Descendants().Where(x=>x.Name.LocalName=="item").ToDictionary(x=>(string)x.Attribute("id"),x=>x);
    int total=0;
    foreach(var ir in pkg.Descendants().Where(x=>x.Name.LocalName=="itemref")){
     XElement item;if(!items.TryGetValue((string)ir.Attribute("idref"),out item))continue;
     if((string)ir.Attribute("linear")=="no")continue;
     string href=Resolve(opf,(string)item.Attribute("href"));string markup=ZipText(z,href);total+=markup.Length;if(total>60*1024*1024)throw new Exception("EPUB 解压后的文字过大。");
     var m=Regex.Match(markup,@"<h[1-3]\b[^>]*>([\s\S]*?)</h[1-3]>",RegexOptions.IgnoreCase);string text=Clean(markup,true);
     if(text.Length>0)b.Chapters.Add(new Chapter{Id=href,Title=m.Success?Clean(m.Groups[1].Value,true):"第 "+(b.Chapters.Count+1)+" 节",Text=text});
    }
    if(b.Chapters.Count==0)throw new Exception("这本 EPUB 没有可提取的文字章节，或受 DRM 保护。");
   }
  }
 }
 public static class Locator {
  public static string Format(string id,double fraction){return "r1:"+Store.Clamp(fraction,0,1).ToString("F4",CultureInfo.InvariantCulture)+":"+id;}
  public static bool Parse(string value,out string id,out double fraction){
   id="";fraction=0;if(value==null)return false;var m=Regex.Match(value,@"^r1:([^:]+):(.+)$");
   if(m.Success){id=m.Groups[2].Value;if(!double.TryParse(m.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out fraction))return false;fraction=Store.Clamp(fraction,0,1);return true;}
   if(value.StartsWith("epubcfi("))return false;id=value;return id.Length>0;
  }
 }
}
