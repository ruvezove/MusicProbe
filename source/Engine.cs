using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MusicProbe {
public sealed class Settings {
    public string Folder = "", CopiesFolder = "", ResortingFolder = "";
    public bool AutoCopies = true, GlobalExact = false, MoveVariants = true, AutoResorting = true;
    public int ProbeJobs = 6, HashJobs = 2, FullJobs = 1, PartialKB = 512, MaxName = 180, Timeout = 30000;
    public double DurationTolerance = 1;
    public void Validate() {
        if (new[]{ProbeJobs,HashJobs,FullJobs}.Any(v=>v<1||v>128)) throw new Exception("Количество потоков: от 1 до 128.");
        if (PartialKB<4||PartialKB>16384) throw new Exception("Чтение с края: от 4 до 16384 КБ.");
        if (MaxName<20||MaxName>240) throw new Exception("Длина имени: от 20 до 240 символов.");
        if (Timeout<1000||Timeout>600000) throw new Exception("Тайм-аут: от 1000 до 600000 мс.");
        if (double.IsNaN(DurationTolerance)||double.IsInfinity(DurationTolerance)||DurationTolerance<0||DurationTolerance>3600) throw new Exception("Допуск длительности: от 0 до 3600 секунд.");
    }
}
public sealed class Track {
    public string Name, Target, Artist, Title, Codec, Error = "", FullHash, PartialHash;
    public bool CanRename, Variant;
    public int Bits, Rate;
    public double Duration, Bitrate;
    public long Size, Stamp, Date;
    public int Keep = -1;
}
public sealed class Operation {
    public string Source, Destination;
    public bool Copy, Resort;
    public Track Track;
}
public sealed class ResultRow {
    public string File {get;set;}
    public string Action {get;set;}
    public string Reason {get;set;}
    public string Result {get;set;}
    public string Quality {get;set;}
    public string Kind {get;set;}
}
public sealed class Plan {
    public string Folder, CopiesFolder, ResortingFolder;
    public Track[] Tracks;
    public Operation[] Operations;
    public ResultRow[] Rows;
    public double Seconds;
}
public sealed class ProgressState {
    public string Text;
    public double Fraction;
}
public static class Engine {
    static readonly HashSet<string> Extensions = new HashSet<string>(new[]{".mp3",".flac",".ogg",".m4a",".aac",".wav",".wma",".opus",".ape",".alac",".aiff",".aif",".dsf",".dff",".wv",".tak"},StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Lossless = new HashSet<string>(new[]{"flac","alac","ape","wavpack","tta","tak","truehd","dsd_lsbf","dsd_msbf"});
    public static string Normalize(string value) { return Regex.Replace((value??"").Normalize(NormalizationForm.FormKC),@"\s+"," ").Trim().ToLowerInvariant(); }
    public static string TitleKey(string title) {
        string key=Normalize(Regex.Replace((title??"").Normalize(NormalizationForm.FormKC),@"\p{P}",""));
        return key.Length>0?key:Normalize(title);
    }
    public static string Clean(string value, int max) {
        string s = Regex.Replace((value??"").Normalize(NormalizationForm.FormKC),"[\\\\/:*?\"<>|\\x00-\\x1f]","_");
        s = Regex.Replace(s,@"\s+"," ").Trim().TrimEnd('.',' ');
        if (s.Length==0) s="Unknown";
        if (Regex.IsMatch(s,@"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?$",RegexOptions.IgnoreCase)) s="_"+s;
        if (s.Length>max) { s=s.Substring(0,max); if(char.IsHighSurrogate(s[s.Length-1])) s=s.Substring(0,s.Length-1); }
        return s.TrimEnd('.',' ');
    }
    public static string FindProbe() {
        foreach(string raw in (Environment.GetEnvironmentVariable("PATH")??"").Split(';')) {
            string dir=raw.Trim().Trim('"'); if(dir.Length==0) continue;
            try { string file=Path.Combine(dir,"ffprobe.exe"); if(File.Exists(file)) return Path.GetFullPath(file); } catch(ArgumentException) { }
        }
        throw new Exception("ffprobe.exe не найден в PATH Windows. Добавьте папку с ffprobe в PATH и перезапустите MusicProbe.");
    }
    static Dictionary<string,object> Map(object value) { return value as Dictionary<string,object> ?? new Dictionary<string,object>(); }
    static string Str(Dictionary<string,object> d,string key) { object v; return d.TryGetValue(key,out v)&&v!=null?Convert.ToString(v,CultureInfo.InvariantCulture):""; }
    static double Number(string s) { double n; return double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&!double.IsInfinity(n)&&!double.IsNaN(n)?n:0; }
    static string ExecuteProbe(string bin,string file,int timeout,CancellationToken token) {
        var si=new ProcessStartInfo(bin,"-v error -select_streams a:0 -show_entries format=duration,bit_rate:format_tags:stream=codec_name,bits_per_sample,bits_per_raw_sample,sample_rate,bit_rate,duration:stream_tags -of json \""+file+"\"") {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
        };
        using(var p=new Process {StartInfo=si}) {
            p.Start();
            Task<string> stdout=p.StandardOutput.ReadToEndAsync(), stderr=p.StandardError.ReadToEndAsync();
            var clock=Stopwatch.StartNew();
            try {
                while(!p.WaitForExit(40)) { token.ThrowIfCancellationRequested(); if(clock.ElapsedMilliseconds>timeout) throw new TimeoutException("ffprobe превысил тайм-аут."); }
                token.ThrowIfCancellationRequested();
                string output=stdout.GetAwaiter().GetResult(), error=stderr.GetAwaiter().GetResult();
                if(p.ExitCode!=0) throw new Exception(string.IsNullOrWhiteSpace(error)?"ffprobe не смог прочитать файл.":error.Trim());
                return output;
            } finally { if(!p.HasExited) { p.Kill(); p.WaitForExit(); } }
        }
    }
    static Track Probe(string path,Settings settings,string bin,CancellationToken token) {
        var info=new FileInfo(path);
        var t=new Track {Name=info.Name,Target=info.Name,Size=info.Length,Stamp=info.LastWriteTimeUtc.Ticks,Artist="Unknown Artist",Title=Path.GetFileNameWithoutExtension(path),Codec=""};
        try {
            var data=Map(new JavaScriptSerializer().DeserializeObject(ExecuteProbe(bin,path,settings.Timeout,token)));
            object value; var format=data.TryGetValue("format",out value)?Map(value):Map(null);
            var streams=data.TryGetValue("streams",out value)?value as object[]:null;
            if(streams==null||streams.Length==0) throw new Exception("Аудиопоток не найден.");
            var audio=Map(streams[0]); var tags=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
            foreach(var source in new[]{format,audio}) if(source.TryGetValue("tags",out value)) foreach(var pair in Map(value)) tags[pair.Key]=pair.Value;
            string raw=Str(tags,"artist"); if(raw.Length==0) raw=Str(tags,"album_artist"); if(raw.Length==0) raw=Str(tags,"albumartist");
            var seen=new HashSet<string>(); string artist=string.Join(";",raw.Split(';','\0').Select(x=>x.Trim()).Where(x=>x.Length>0&&seen.Add(Normalize(x))));
            if(artist.Length>0) t.Artist=artist;
            string title=Str(tags,"title").Trim(); t.CanRename=title.Length>0; if(t.CanRename)t.Title=title;
            t.Codec=Str(audio,"codec_name").ToLowerInvariant();
            t.Duration=Number(Str(audio,"duration")); if(t.Duration<=0)t.Duration=Number(Str(format,"duration"));
            t.Bits=(int)Number(Str(audio,"bits_per_raw_sample")); if(t.Bits<=0)t.Bits=(int)Number(Str(audio,"bits_per_sample"));
            t.Rate=(int)Number(Str(audio,"sample_rate")); if(t.Rate<=0&&t.Duration<=0)throw new Exception("Не удалось прочитать параметры аудиопотока."); t.Bitrate=Number(Str(audio,"bit_rate")); if(t.Bitrate<=0)t.Bitrate=Number(Str(format,"bit_rate"));
            foreach(string key in new[]{"date","originaldate","releasedate","year","originalyear","tdrc","creation_date"}) {
                string date=Str(tags,key); DateTime parsed;
                if(Regex.IsMatch(date,@"^\d{4}$"))date+="-01-01";
                if(DateTime.TryParse(date,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out parsed)) {t.Date=parsed.Ticks;break;}
            }
            if(t.CanRename)t.Target=Clean(t.Artist+" - "+t.Title,settings.MaxName)+info.Extension.ToLowerInvariant();
        } catch(OperationCanceledException) { throw; } catch(Exception ex) {t.Error=ex.Message.Length>500?ex.Message.Substring(0,500):ex.Message;}
        return t;
    }
    public static string Hash(string path,int edgeBytes,CancellationToken token) {
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024))
        using(var sha=SHA256.Create()) {
            var buffer=new byte[1024*1024]; long size=file.Length;
            long[] starts=edgeBytes>0&&size>2L*edgeBytes?new[]{0L,size-edgeBytes}:new[]{0L};
            foreach(long start in starts) {
                file.Position=start; long left=starts.Length==2?edgeBytes:size;
                while(left>0) {token.ThrowIfCancellationRequested();int n=file.Read(buffer,0,(int)Math.Min(left,buffer.Length)); if(n==0)throw new EndOfStreamException("Файл изменился при чтении.");sha.TransformBlock(buffer,0,n,buffer,0);left-=n;}
            }
            sha.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(sha.Hash).Replace("-","");
        }
    }
    static bool IsLossless(Track t) { return t.Codec.StartsWith("pcm_",StringComparison.Ordinal)||Lossless.Contains(t.Codec); }
    static int Quality(Track a,Track b) {
        int r=IsLossless(b).CompareTo(IsLossless(a)); if(r!=0)return r;
        r=b.Bits.CompareTo(a.Bits);if(r!=0)return r;r=b.Rate.CompareTo(a.Rate);if(r!=0)return r;
        if(!IsLossless(a)&&!IsLossless(b)){r=b.Bitrate.CompareTo(a.Bitrate);if(r!=0)return r;}
        r=b.Date.CompareTo(a.Date);return r!=0?r:StringComparer.OrdinalIgnoreCase.Compare(a.Name,b.Name);
    }
    static void ParallelPhase(int[] ids,int jobs,CancellationToken token,Action<int> action,Action<ProgressState> report,string stage,double start,double span) {
        int done=0; var clock=Stopwatch.StartNew();long last=-100;object gate=new object();
        Parallel.ForEach(ids,new ParallelOptions {MaxDegreeOfParallelism=jobs,CancellationToken=token},i=>{
            action(i);int n=Interlocked.Increment(ref done);
            lock(gate) {if(n==ids.Length||clock.ElapsedMilliseconds-last>=90){last=clock.ElapsedMilliseconds;if(report!=null)report(new ProgressState {Text=string.Format("{0} · {1} / {2}",stage,n,ids.Length),Fraction=start+span*n/Math.Max(1,ids.Length)});}}
        });
    }
    public static Plan Analyze(Settings settings,CancellationToken token,Action<ProgressState> report) {
        settings.Validate();var clock=Stopwatch.StartNew();string dir=Path.GetFullPath(settings.Folder).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!Directory.Exists(dir))throw new Exception("Выбранная папка не существует.");
        string copies=Path.GetFullPath(settings.AutoCopies?Path.Combine(dir,"Duplicates"):settings.CopiesFolder).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(string.Equals(dir,copies,StringComparison.OrdinalIgnoreCase))throw new Exception("Папка копий должна отличаться от исходной.");
        string resorting=Path.GetFullPath(settings.AutoResorting||!settings.MoveVariants?Path.Combine(dir,"Resorting"):settings.ResortingFolder).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(settings.MoveVariants&&(string.Equals(dir,resorting,StringComparison.OrdinalIgnoreCase)||string.Equals(copies,resorting,StringComparison.OrdinalIgnoreCase)))throw new Exception("Папка Resorting должна отличаться от исходной папки и папки точных копий.");
        var paths=Directory.EnumerateFiles(dir).Where(p=>Extensions.Contains(Path.GetExtension(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)==0).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
        token.ThrowIfCancellationRequested();var tracks=new Track[paths.Length];string bin=paths.Length>0?FindProbe():"";
        ParallelPhase(Enumerable.Range(0,paths.Length).ToArray(),settings.ProbeJobs,token,i=>tracks[i]=Probe(paths[i],settings,bin,token),report,"Чтение тегов",0,.55);
        var groups=Enumerable.Range(0,tracks.Length).Where(i=>tracks[i].Error.Length==0).GroupBy(i=>Normalize(tracks[i].Artist)+"\0"+TitleKey(tracks[i].Title)).Select(g=>g.ToArray()).ToArray();
        var candidates=new List<int[]>();
        foreach(var group in groups) {
            var sorted=group.OrderBy(i=>tracks[i].Duration).ThenBy(i=>tracks[i].Name,StringComparer.OrdinalIgnoreCase).ToArray();var cluster=new List<int>();
            foreach(int i in sorted) {
                if(cluster.Count>0&&(tracks[i].Duration<=0||tracks[cluster[0]].Duration<=0||tracks[i].Duration-tracks[cluster[0]].Duration>settings.DurationTolerance)){if(cluster.Count>1)candidates.Add(cluster.ToArray());cluster.Clear();}
                cluster.Add(i);
            } if(cluster.Count>1)candidates.Add(cluster.ToArray());
        }
        if(settings.GlobalExact)candidates.AddRange(Enumerable.Range(0,tracks.Length).Where(i=>tracks[i].Error.Length==0).GroupBy(i=>tracks[i].Size).Where(g=>g.Count()>1).Select(g=>g.ToArray()));
        int[] ids=candidates.SelectMany(g=>g).Distinct().ToArray();
        ParallelPhase(ids,settings.HashJobs,token,i=>{try{tracks[i].PartialHash=Hash(paths[i],settings.PartialKB*1024,token);if(tracks[i].Size<=2L*settings.PartialKB*1024)tracks[i].FullHash=tracks[i].PartialHash;}catch(OperationCanceledException){throw;}catch(Exception ex){tracks[i].Error=ex.Message;}},report,"Проверка копий",.55,.20);
        var collisions=candidates.SelectMany(g=>g.Where(i=>tracks[i].PartialHash!=null).GroupBy(i=>tracks[i].Size+":"+tracks[i].PartialHash).Where(h=>h.Count()>1).Select(h=>h.ToArray())).ToArray();
        ids=collisions.SelectMany(g=>g).Distinct().Where(i=>tracks[i].FullHash==null).ToArray();
        ParallelPhase(ids,settings.FullJobs,token,i=>{try{tracks[i].FullHash=Hash(paths[i],0,token);}catch(OperationCanceledException){throw;}catch(Exception ex){tracks[i].Error=ex.Message;}},report,"Полный SHA-256",.75,.24);
        int[] parent=Enumerable.Range(0,tracks.Length).ToArray();Func<int,int> find=null;find=i=>{while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;};
        foreach(var cluster in collisions)foreach(var exact in cluster.Where(i=>tracks[i].Error.Length==0&&tracks[i].FullHash!=null).GroupBy(i=>tracks[i].FullHash)) {int first=exact.First();foreach(int i in exact)parent[find(i)]=find(first);}
        foreach(var exact in Enumerable.Range(0,tracks.Length).GroupBy(find).Where(g=>g.Count()>1)) {var order=exact.ToArray();Array.Sort(order,(a,b)=>Quality(tracks[a],tracks[b]));foreach(int i in order.Skip(1))tracks[i].Keep=order[0];}
        foreach(var group in groups) {
            var alive=group.Where(i=>tracks[i].Keep<0&&tracks[i].Error.Length==0).ToArray();
            Array.Sort(alive,(a,b)=>{int r=tracks[a].Duration.CompareTo(tracks[b].Duration);return r!=0?r:Quality(tracks[a],tracks[b]);});
            if(alive.Length>1)for(int n=0;n<alive.Length;n++) {var t=tracks[alive[n]];t.Variant=true;t.Target=Clean((n+1)+"_"+Path.GetFileNameWithoutExtension(t.Target),settings.MaxName)+Path.GetExtension(t.Target);}
        }
        foreach(var t in tracks)if(t.Error.Length>0){t.Keep=-1;t.Target=t.Name;}
        var changing=new HashSet<string>(tracks.Where(t=>t.Keep>=0||(settings.MoveVariants&&t.Variant)||t.Target!=t.Name).Select(t=>t.Name),StringComparer.OrdinalIgnoreCase);
        var occupied=new HashSet<string>(Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Where(n=>!changing.Contains(n)),StringComparer.OrdinalIgnoreCase);
        var copyNames=new HashSet<string>(Directory.Exists(copies)?Directory.EnumerateFileSystemEntries(copies).Select(Path.GetFileName):Enumerable.Empty<string>(),StringComparer.OrdinalIgnoreCase);
        var resortNames=new HashSet<string>(Directory.Exists(resorting)?Directory.EnumerateFileSystemEntries(resorting).Select(Path.GetFileName):Enumerable.Empty<string>(),StringComparer.OrdinalIgnoreCase);
        var operations=new List<Operation>();var rows=new List<ResultRow>();
        foreach(var t in tracks) {
            string action="Оставить",reason=t.CanRename?"Имя уже подходит":"Нет тега названия",result=t.Name,kind="keep";
            if(t.Error.Length>0){action="Пропустить";reason=t.Error;kind="error";}
            else if(t.Keep>=0) {
                string name=Unique(t.Name,copyNames,settings.MaxName);result=Path.Combine(copies,name);action="Переместить";reason="Точная копия: "+tracks[t.Keep].Name;kind="copy";
                operations.Add(new Operation {Source=Path.Combine(dir,t.Name),Destination=result,Copy=true,Track=t});
            } else if(settings.MoveVariants&&t.Variant) {
                t.Target=Unique(t.Target,resortNames,settings.MaxName);result=Path.Combine(resorting,t.Target);action="В Resorting";reason="Одинаковые исполнитель и название без учёта пунктуации; разные файлы";kind="resort";
                operations.Add(new Operation {Source=Path.Combine(dir,t.Name),Destination=result,Resort=true,Track=t});
            } else if(t.Target!=t.Name) {
                t.Target=Unique(t.Target,occupied,settings.MaxName);result=t.Target;
                if(t.Target!=t.Name){action="Переименовать";reason="Имя по тегам";kind="rename";operations.Add(new Operation {Source=Path.Combine(dir,t.Name),Destination=Path.Combine(dir,t.Target),Track=t});}
            }
            rows.Add(new ResultRow {File=t.Name,Action=action,Reason=reason,Result=result,Kind=kind,Quality=string.Format(CultureInfo.InvariantCulture,"{0} · {1:0.#} с · {2:0.##} МБ",t.Codec.ToUpperInvariant(),t.Duration,t.Size/1048576.0)});
        }
        token.ThrowIfCancellationRequested();return new Plan {Folder=dir,CopiesFolder=copies,ResortingFolder=resorting,Tracks=tracks,Operations=operations.ToArray(),Rows=rows.ToArray(),Seconds=clock.Elapsed.TotalSeconds};
    }
    static string Unique(string name,HashSet<string> occupied,int max) {
        string candidate=name;int n=2;while(occupied.Contains(candidate)){string suffix=" ("+(n++)+")";candidate=Clean(Path.GetFileNameWithoutExtension(name),max-suffix.Length)+suffix+Path.GetExtension(name);}occupied.Add(candidate);return candidate;
    }
    static void Transfer(string from,string to) {
        try{File.Move(from,to);return;}catch(IOException ex){if((ex.HResult&0xffff)!=17)throw;}
        using(var input=new FileStream(from,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var output=new FileStream(to,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
            try{input.CopyTo(output);output.Flush(true);}catch{output.Dispose();File.Delete(to);throw;}
        }
        try{if(Hash(from,0,CancellationToken.None)!=Hash(to,0,CancellationToken.None))throw new IOException("Проверка переноса не пройдена.");File.Delete(from);}catch{File.Delete(to);throw;}
    }
    public static string Apply(Plan plan,Action<ProgressState> report) {
        foreach(var t in plan.Tracks) {
            var f=new FileInfo(Path.Combine(plan.Folder,t.Name));
            if(!f.Exists||f.Length!=t.Size||f.LastWriteTimeUtc.Ticks!=t.Stamp)throw new IOException("Файл изменился: "+t.Name+". Выполните анализ заново.");
            if(t.FullHash!=null&&Hash(f.FullName,0,CancellationToken.None)!=t.FullHash)throw new IOException("Содержимое изменилось: "+t.Name+". Выполните анализ заново.");
        }
        foreach(string folder in plan.Operations.Where(o=>o.Copy||o.Resort).Select(o=>Path.GetDirectoryName(o.Destination)).Distinct(StringComparer.OrdinalIgnoreCase)) {
            if(Directory.Exists(folder)&&(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка назначения является ссылкой. Выберите обычную папку.");
            Directory.CreateDirectory(folder);
        }
        var sources=new HashSet<string>(plan.Operations.Select(o=>o.Source),StringComparer.OrdinalIgnoreCase);
        foreach(var op in plan.Operations)if((File.Exists(op.Destination)||Directory.Exists(op.Destination))&&!sources.Contains(op.Destination))throw new IOException("Имя уже занято: "+op.Destination+". Выполните анализ заново.");
        var staged=new List<Tuple<Operation,string>>();var committed=new List<Tuple<Operation,string>>();
        try {
            foreach(var op in plan.Operations) {
                string temp=Path.Combine(plan.Folder,".musicprobe-"+Guid.NewGuid().ToString("N")+".tmp");File.Move(op.Source,temp);staged.Add(Tuple.Create(op,temp));
            }
            foreach(var pair in staged) {
                Transfer(pair.Item2,pair.Item1.Destination);committed.Add(pair);
                if(report!=null)report(new ProgressState {Text=string.Format("Применение · {0} / {1}",committed.Count,staged.Count),Fraction=(double)committed.Count/Math.Max(1,staged.Count)});
            }
        } catch(Exception ex) {
            var failures=new List<string>();
            foreach(var pair in committed.AsEnumerable().Reverse())try{Transfer(pair.Item1.Destination,pair.Item2);}catch(Exception e){failures.Add(pair.Item1.Destination+": "+e.Message);}
            foreach(var pair in staged.AsEnumerable().Reverse())if(File.Exists(pair.Item2))try{File.Move(pair.Item2,pair.Item1.Source);}catch(Exception e){failures.Add(pair.Item2+": "+e.Message);}
            throw new IOException(ex.Message+(failures.Count>0?"\nНе удалось восстановить:\n"+string.Join("\n",failures):"\nИсходные файлы восстановлены."),ex);
        }
        return string.Format("Готово. Точных копий: {0}. В Resorting: {1}. Переименовано на месте: {2}.",plan.Operations.Count(o=>o.Copy),plan.Operations.Count(o=>o.Resort),plan.Operations.Count(o=>!o.Copy&&!o.Resort));
    }
}
}
