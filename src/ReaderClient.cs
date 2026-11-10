using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneLine {
 public class ReaderApiException:Exception {
  public readonly int Status;
  public ReaderApiException(int status,string message):base(message){Status=status;}
 }
 public class RemotePosition {
  public string Locator="",ChapterTitle="",Device="";
  public double Percentage;
  public long UpdatedAt;
  public static RemotePosition From(object p){return p==null?null:new RemotePosition{Locator=Json.Str(p,"locator"),ChapterTitle=Json.Str(p,"chapterTitle"),Device=Json.Str(p,"device"),Percentage=Json.Num(p,"percentage"),UpdatedAt=(long)Json.Num(p,"updatedAt")};}
 }
 public class ShelfPage {public List<Book> Books=new List<Book>();public int Total;}
 public class ReaderClient:IDisposable {
  readonly Store store;readonly HttpClient http;readonly SemaphoreSlim refreshGate=new SemaphoreSlim(1,1);
  public ReaderClient(Store s,HttpMessageHandler handler=null){
   store=s;
   // csc-built Framework applications can otherwise inherit legacy TLS defaults.
   // Keep Windows certificate/hostname validation; only select modern TLS 1.2.
   ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
   http=handler==null?new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate}):new HttpClient(handler);
   http.Timeout=TimeSpan.FromSeconds(25);http.DefaultRequestHeaders.UserAgent.ParseAdd("OneLineReader/1.0.4");
   http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
  }
  public bool SignedIn{get{return !string.IsNullOrEmpty(store.State.Account.UserId)&&!string.IsNullOrEmpty(Store.Unprotect(store.State.Account.Access));}}
  public static string NormalizeServer(string url){
   url=(url??"").Trim().TrimEnd('/');if(url.EndsWith("/api/v1",StringComparison.OrdinalIgnoreCase))url=url.Substring(0,url.Length-7).TrimEnd('/');
   Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||(uri.Scheme!="http"&&uri.Scheme!="https")||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)throw new Exception("请输入完整的 HTTP/HTTPS 服务地址，不要附带账号或查询参数。");
   return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
  }
  async Task<HttpResponseMessage> Send(string server,string path,string method,object body,string token){
   var req=new HttpRequestMessage(new HttpMethod(method),server+"/api/v1"+path){Version=HttpVersion.Version11};
   req.Headers.ExpectContinue=false;
   if(token.Length>0)req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
   if(body!=null){var bytes=new UTF8Encoding(false).GetBytes(Json.Stringify(body));req.Content=new ByteArrayContent(bytes);req.Content.Headers.ContentType=new MediaTypeHeaderValue("application/json");req.Content.Headers.ContentLength=bytes.Length;}
   try{return await http.SendAsync(req).ConfigureAwait(false);}catch(TaskCanceledException){throw new Exception("Reader 请求超时："+method+" /api/v1"+path.Split('?')[0]+"。本机进度仍保留。");}catch(HttpRequestException ex){
    Exception cause=ex;WebException web=null;while(cause!=null){if(cause is WebException)web=(WebException)cause;cause=cause.InnerException;}
    string detail;
    if(web!=null&&(web.Status==WebExceptionStatus.TrustFailure||web.Status==WebExceptionStatus.SecureChannelFailure))detail="HTTPS/TLS 连接失败。已启用 TLS 1.2，请检查证书是否覆盖此域名、证书链是否完整，以及该端口是否提供 HTTPS。";
    else if(web!=null&&web.Status==WebExceptionStatus.NameResolutionFailure)detail="Reader 域名无法解析，请检查域名或 DNS。";
    else detail="无法连接 Reader。请检查地址的 http/https、端口与网络。";
    throw new Exception(detail+"\n请求："+method+" "+server+"/api/v1"+path.Split('?')[0]+"\n本机进度仍保留。");
   }finally{req.Dispose();}
  }
  async Task<string> Read(HttpResponseMessage response){
   using(response){
    string text=await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    if((int)response.StatusCode>=300){
     string msg="",code="";try{var parsed=Json.Parse(text);var error=Json.Get(parsed,"error");code=Json.Str(error,"code");msg=Json.Str(error,"message");if(msg.Length==0&&error is string)msg=(string)error;if(msg.Length==0)msg=Json.Str(parsed,"message");if(msg.Length==0)msg=Json.Str(parsed,"detail");}catch{}
     int status=(int)response.StatusCode;string lower=text.ToLowerInvariant();
     if(status==400&&lower.Contains("plain http")&&lower.Contains("https"))msg="当前端口只接受 HTTPS，却收到了 HTTP 请求。请填写你的 Reader 服务的 HTTPS 地址。";
     if(string.IsNullOrWhiteSpace(msg)){
      var title=System.Text.RegularExpressions.Regex.Match(text,@"<title\b[^>]*>([\s\S]*?)</title>",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      if(title.Success)msg=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(title.Groups[1].Value,@"<[^>]+>",""));
      else if(!text.Contains("<")&&text.Trim().Length>0)msg=text.Trim();
      else msg="服务器或反向代理拒绝了请求，未返回 Reader 的 JSON 错误。请检查域名、协议和端口。";
     }
     msg=msg.Replace('\r',' ').Replace('\n',' ').Trim();if(msg.Length>400)msg=msg.Substring(0,400)+"…";
     if(status==401)msg="登录已失效或账号密码不正确，请重新登录。";
     if(status>=300&&status<400){msg="服务发生重定向，请填写最终的 HTTPS Reader 地址。";if(response.Headers.Location!=null&&response.Headers.Location.IsAbsoluteUri&&response.Headers.Location.UserInfo.Length==0)msg+=" 目标："+response.Headers.Location.GetLeftPart(UriPartial.Path);}
     string endpoint="";if(response.RequestMessage!=null&&response.RequestMessage.RequestUri!=null)endpoint="\n请求："+response.RequestMessage.Method+" "+response.RequestMessage.RequestUri.GetLeftPart(UriPartial.Path);
     throw new ReaderApiException(status,msg+"（HTTP "+status+(code.Length>0?" / "+code:"")+"）"+endpoint);
    }
    return text;
   }
  }
  async Task<string> Request(string path,string method="GET",object body=null,bool auth=true){
   var account=store.State.Account;string server=NormalizeServer(account.Server),scope=account.Scope;
   if(auth&&!SignedIn)throw new Exception("请先在 Reader 同步中登录。");
   if(auth&&account.AccessExpires>0&&account.AccessExpires<Store.Now+60000)await Refresh(server,scope).ConfigureAwait(false);
   string token=auth?Store.Unprotect(account.Access):"";
   var response=await Send(server,path,method,body,token).ConfigureAwait(false);
   if(auth&&response.StatusCode==HttpStatusCode.Unauthorized&&account.Refresh.Length>0){response.Dispose();await Refresh(server,scope,token).ConfigureAwait(false);response=await Send(server,path,method,body,Store.Unprotect(account.Access)).ConfigureAwait(false);}
   return await Read(response).ConfigureAwait(false);
  }
  async Task Refresh(string server,string scope,string failedToken=null){
   await refreshGate.WaitAsync().ConfigureAwait(false);
   try{
    var a=store.State.Account;if(a.Scope!=scope)throw new Exception("账号已切换，请重试。");
    if(failedToken!=null&&Store.Unprotect(a.Access)!=failedToken)return;
    if(failedToken==null&&a.AccessExpires>Store.Now+60000)return;
    string refresh=Store.Unprotect(a.Refresh);if(refresh.Length==0)throw new Exception("登录已失效，请重新登录。");
    string text=await Read(await Send(server,"/auth/refresh","POST",new{refreshToken=refresh},"").ConfigureAwait(false)).ConfigureAwait(false);
    var s=Json.Parse(text);if(Json.Get(s,"session")!=null)s=Json.Get(s,"session");
    ApplySession(a,s);store.Save(); // Persist rotated refresh token before the next request.
   }finally{refreshGate.Release();}
  }
  static void ApplySession(Account a,object s){
   string access=Json.Str(s,"accessToken"),refresh=Json.Str(s,"refreshToken");if(access.Length==0||refresh.Length==0)throw new Exception("服务未返回有效会话，请检查 Reader 版本。");
   a.Access=Store.Protect(access);a.Refresh=Store.Protect(refresh);a.AccessExpires=(long)Json.Num(s,"accessTokenExpiresAt");
   var user=Json.Get(s,"user");if(user!=null){a.UserId=Json.Str(user,"id");a.Username=Json.Str(user,"username");}
  }
  public async Task<string> Probe(string url){
   string server=NormalizeServer(url);string text;
   try{text=await Read(await Send(server,"/instance","GET",null,"").ConfigureAwait(false)).ConfigureAwait(false);}catch(ReaderApiException ex){throw new ReaderApiException(ex.Status,"服务检测失败（尚未提交账号密码）：\n"+ex.Message);}
   object obj;
   try{obj=Json.Parse(text);}catch{throw new Exception("该地址没有返回 Reader API，请检查端口、反向代理路径或版本。");}
   if(Json.Num(obj,"apiVersion")!=1)throw new Exception("暂不支持此 Reader API 版本；本应用对接 /api/v1。");return Json.Str(obj,"name")+" · API v1";
  }
  public async Task Login(string url,string username,string password){
   username=(username??"").Trim();if(username.Length==0||string.IsNullOrEmpty(password))throw new Exception("请填写用户名和密码。");
   if(username.Length>100||password.Length>1024)throw new Exception("用户名或密码超过 Reader 允许的长度。");
   string server=NormalizeServer(url);await Probe(server).ConfigureAwait(false);
   string text;try{text=await Read(await Send(server,"/auth/login","POST",new{username=username,password=password},"").ConfigureAwait(false)).ConfigureAwait(false);}catch(ReaderApiException ex){throw new ReaderApiException(ex.Status,"登录接口失败：\n"+ex.Message.Replace(password,"[密码已隐藏]"));}
   var session=Json.Parse(text);if(Json.Get(session,"session")!=null)session=Json.Get(session,"session");
   var account=new Account{Server=server};ApplySession(account,session);if(account.UserId.Length==0)throw new Exception("服务未返回用户标识。");store.State.Account=account;store.Save();
  }
  public async Task Logout(){
   var a=store.State.Account;
   try{if(SignedIn)await Request("/auth/logout","POST",new{refreshToken=Store.Unprotect(a.Refresh)}).ConfigureAwait(false);}finally{a.Access="";a.Refresh="";a.AccessExpires=0;store.Save();}
  }
  public async Task Heartbeat(){
   var account=store.State.Account;
   var response=Json.Parse(await Request("/auth/me").ConfigureAwait(false));
   if(!object.ReferenceEquals(account,store.State.Account))return;
   if(Json.Str(Json.Get(response,"user"),"id")!=account.UserId)throw new Exception("Reader 握手返回的账号不匹配，请重新登录。");
  }
  public async Task<ShelfPage> Shelf(string scope,string search,int page){
   string accountScope=store.State.Account.Scope;
   var obj=Json.Parse(await Request("/books?scope="+Uri.EscapeDataString(scope)+"&search="+Uri.EscapeDataString(search??"")+"&page="+page+"&pageSize=50").ConfigureAwait(false));
   var result=new ShelfPage{Total=(int)Json.Num(obj,"total")};
   foreach(var x in Json.List(Json.Get(obj,"items")))result.Books.Add(new Book{Id=Json.Str(x,"id"),Title=Json.Str(x,"title"),Author=Json.Str(x,"author"),Format=Json.Str(x,"format"),Scope=accountScope,OnShelf=Json.Str(x,"shelfState")!="off"});
   return result;
  }
  public async Task LoadChapters(Book b){
   RequireScope(b);string path="/books/"+Uri.EscapeDataString(b.Id);
   if(!b.OnShelf){
    var added=Json.Parse(await Request("/library/browse/shelf","POST",new{bookIds=new[]{b.Id},action="add"}).ConfigureAwait(false));
    if(Json.List(Json.Get(added,"failed")).Count>0)throw new Exception("Reader 无法把此书加入个人书架，请先在网页检查文件状态。");
    b.OnShelf=true;
   }
   var manifest=Json.Parse(await Request(path+"/manifest").ConfigureAwait(false));var content=Json.Get(manifest,"content")??manifest;
   string kind=Json.Str(content,"kind");if(kind!="text"&&kind!="reflowable")throw new Exception("单行阅读支持 TXT、EPUB 和文字章节。这本书的格式为 "+b.Format+"。");
   var all=Json.Parse(await Request(path+"/items").ConfigureAwait(false));
   var chapters=new List<Chapter>();
   foreach(var x in Json.List(Json.Get(all,"items"))){
    if(Json.Str(x,"kind")=="page")continue;
    string id=Json.Str(x,"href"),resource=Json.Str(x,"resourceRef");if(resource.Length==0)resource=id;
    chapters.Add(new Chapter{Id=id,Ref=resource,Title=Json.Str(x,"title"),MediaType=Json.Str(x,"mediaType")});
   }
   if(chapters.Count==0){
    // Unstructured TXT: preserve its server section identity instead of inventing chapters.
    if(kind!="text")throw new Exception("服务没有返回可阅读的文字章节。");
    string text=await Request(path+"/content").ConfigureAwait(false);chapters.Add(new Chapter{Id="chunk-0",Title="正文",Text=TextBook.Clean(text,false)});
   }
   RequireScope(b);b.Chapters=chapters;store.Save();
  }
  public async Task<string> ChapterText(Book b,Chapter c){
   if(c.Text!=null)return c.Text;
   string cached=store.ReadCache(b,c);if(cached!=null){c.Text=cached;return cached;}
   RequireScope(b);string raw=await Request("/books/"+Uri.EscapeDataString(b.Id)+"/assets?ref="+Uri.EscapeDataString(c.Ref)).ConfigureAwait(false);
   RequireScope(b);bool html=c.MediaType.IndexOf("html",StringComparison.OrdinalIgnoreCase)>=0||RegexLooksHtml(raw);
   c.Text=TextBook.Clean(raw,html);if(c.Text.Length==0)c.Text="（本章节没有可显示的文字）";store.WriteCache(b,c,c.Text);return c.Text;
  }
  static bool RegexLooksHtml(string raw){return System.Text.RegularExpressions.Regex.IsMatch(raw,@"<(?:(?:!DOCTYPE|html|body|p|div|br|h[1-6])\b)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);}
  public void RequireScope(Book b){if(b.Remote&&b.Scope!=store.State.Account.Scope)throw new Exception("这本书属于另一个 Reader 账号或服务器，请切换回原账号。");}
  public async Task<RemotePosition> GetProgress(Book b){RequireScope(b);var o=Json.Parse(await Request("/sync/progress/"+Uri.EscapeDataString(b.Id)).ConfigureAwait(false));return RemotePosition.From(Json.Get(o,"progress"));}
  public async Task<RemotePosition> PutProgress(Book b,RemotePosition p){
   RequireScope(b);var o=Json.Parse(await Request("/sync/progress/"+Uri.EscapeDataString(b.Id),"PUT",new{locator=p.Locator,percentage=p.Percentage,chapterTitle=p.ChapterTitle,device="OneLineReader/Windows",updatedAt=p.UpdatedAt}).ConfigureAwait(false));
   var result=RemotePosition.From(Json.Get(o,"progress"));if(result==null)throw new Exception("服务没有确认保存进度，本机待同步状态已保留。");return result;
  }
  public void Dispose(){http.Dispose();refreshGate.Dispose();}
 }
}
