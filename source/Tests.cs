using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MusicProbe {
public static class Tests {
    static int checks;
    static void Assert(bool success,string name){if(!success)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
    static Plan Scan(string path,bool global){return Engine.Analyze(new Settings {Folder=path,MoveVariants=false,GlobalExact=global,PartialKB=4,ProbeJobs=4,HashJobs=3,FullJobs=2},CancellationToken.None,null);}
    static void Audio(string path,string freq,string title) {
        var psi=new ProcessStartInfo("ffmpeg.exe","-v error -f lavfi -i sine=frequency="+freq+":duration=2 -metadata artist=Тест -metadata title=\""+title+"\" -c:a flac \""+path+"\"") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        using(var p=Process.Start(psi)){string error=p.StandardError.ReadToEnd();p.WaitForExit();if(p.ExitCode!=0)throw new Exception(error);}
    }
    static void PumpUntil(Func<bool> done,int milliseconds) {
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(10)};var frame=new DispatcherFrame();var sw=Stopwatch.StartNew();bool timeout=false;
        timer.Tick+=delegate{if(done()||sw.ElapsedMilliseconds>milliseconds){timeout=!done();frame.Continue=false;timer.Stop();}};timer.Start();Dispatcher.PushFrame(frame);if(timeout)throw new Exception("UI timeout");
    }
    [STAThread]public static int Main() {
        string root=Path.Combine(Path.GetTempPath(),"MusicProbe-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            Assert(Engine.Clean("CON",180)=="_CON","Windows reserved names");Assert(Engine.Clean("Ａ / B. ",180)=="A _ B","Unicode normalization and invalid characters");Assert(Engine.Clean("😀abc",3)=="😀a","UTF-16 truncation");
            string hashfile=Path.Combine(root,"hash.txt");File.WriteAllText(hashfile,"abc",new System.Text.UTF8Encoding(false));Assert(Engine.Hash(hashfile,0,CancellationToken.None)=="BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD","SHA-256 known vector");
            string dir=Path.Combine(root,"pipeline");Directory.CreateDirectory(dir);Audio(Path.Combine(dir,"a.flac"),"440","Песня");File.Copy(Path.Combine(dir,"a.flac"),Path.Combine(dir,"copy.flac"));Audio(Path.Combine(dir,"variant.flac"),"880","Песня");
            Plan p=Scan(dir,false);Assert(p.Tracks.Length==3&&p.Operations.Count(o=>o.Copy)==1,"Exact copy detected, different recording kept");Assert(p.Operations.Count(o=>!o.Copy)==2,"Distinct variants numbered");Assert(Directory.GetFiles(dir).Length==3&&!Directory.Exists(Path.Combine(dir,"Duplicates")),"Preview leaves files unchanged");
            var sequential=Engine.Analyze(new Settings {Folder=dir,MoveVariants=false,PartialKB=4,ProbeJobs=1,HashJobs=1,FullJobs=1},CancellationToken.None,null);Assert(string.Join("|",p.Rows.Select(r=>r.File+r.Result))==string.Join("|",sequential.Rows.Select(r=>r.File+r.Result)),"Parallel and sequential plans match");
            string occupied=p.Operations[0].Destination;Directory.CreateDirectory(Path.GetDirectoryName(occupied));File.WriteAllText(occupied,"existing");try{Engine.Apply(p,null);throw new Exception("No conflict error");}catch(IOException){Assert(File.ReadAllText(occupied)=="existing"&&File.Exists(p.Operations[0].Source),"Destination collision never overwrites");}File.Delete(occupied);
            var originals=p.Tracks.ToDictionary(t=>t.Name,t=>Engine.Hash(Path.Combine(dir,t.Name),0,CancellationToken.None));
            try{Engine.Apply(p,progress=>{throw new Exception("Simulated failure");});throw new Exception("No rollback error");}catch(IOException){Assert(originals.All(x=>File.Exists(Path.Combine(dir,x.Key))&&Engine.Hash(Path.Combine(dir,x.Key),0,CancellationToken.None)==x.Value),"Failure restores all original files");}
            Engine.Apply(p,null);Assert(Directory.GetFiles(Path.Combine(dir,"Duplicates")).Length==1&&Directory.GetFiles(dir).Length==2,"Apply moves copy and renames survivors");
            var again=Scan(dir,false);Assert(again.Operations.Length==0,"Repeated analysis is idempotent");
            var changed=Scan(dir,false);string changedFile=Path.Combine(dir,changed.Tracks[0].Name);File.AppendAllText(changedFile,"x");try{Engine.Apply(changed,null);throw new Exception("No stale error");}catch(IOException){Assert(true,"Changed file rejects old plan");}
            var cancellation=new CancellationTokenSource();cancellation.Cancel();try{Engine.Analyze(new Settings{Folder=dir},cancellation.Token,null);throw new Exception("No cancel error");}catch(OperationCanceledException){Assert(true,"Cancellation stops analysis");}
            string tags=Path.Combine(root,"tags");Directory.CreateDirectory(tags);Audio(Path.Combine(tags,"x.flac"),"440","");File.Copy(Path.Combine(tags,"x.flac"),Path.Combine(tags,"y.flac"));Assert(Scan(tags,false).Operations.Length==0,"Files without title are not renamed repeatedly");var global=Scan(tags,true);Assert(global.Operations.Count(o=>o.Copy)==1,"Global exact search finds differently named untagged copies");Engine.Apply(global,null);Assert(Scan(tags,true).Operations.Length==0,"Untagged global search is idempotent");
            string bad=Path.Combine(root,"bad");Directory.CreateDirectory(bad);File.WriteAllText(Path.Combine(bad,"broken.flac"),"not audio");var broken=Scan(bad,true);Assert(broken.Rows.Single().Kind=="error"&&broken.Operations.Length==0,"Unreadable audio remains untouched");
            string custom=Path.Combine(root,"custom");Directory.CreateDirectory(custom);Audio(Path.Combine(custom,"a.flac"),"440","Другая");File.Copy(Path.Combine(custom,"a.flac"),Path.Combine(custom,"b.flac"));var customPlan=Engine.Analyze(new Settings{Folder=custom,AutoCopies=false,CopiesFolder=Path.Combine(root,"copies")},CancellationToken.None,null);Engine.Apply(customPlan,null);Assert(Directory.GetFiles(Path.Combine(root,"copies")).Length==1,"Custom duplicates destination");
            try{Engine.Analyze(new Settings{Folder=custom,AutoCopies=false,CopiesFolder=custom},CancellationToken.None,null);throw new Exception("No same-folder error");}catch(Exception e){Assert(e.Message.Contains("отличаться"),"Copies cannot target source folder");}
            Assert(Engine.TitleKey("Дым сигарет, с ментолом")==Engine.TitleKey("Дым сигарет с ментолом"),"Title grouping ignores commas");
            Assert(Engine.TitleKey("«Дым сигарет!»")==Engine.TitleKey("Дым сигарет"),"Title grouping ignores Unicode punctuation");
            Assert(Engine.TitleKey("Дым сигарет Live")!=Engine.TitleKey("Дым сигарет"),"Different title words remain distinct");
            Assert(Engine.TitleKey("C++")!=Engine.TitleKey("C"),"Meaningful non-punctuation symbols are preserved");
            Assert(Engine.TitleKey("...")!=Engine.TitleKey("!!!"),"Punctuation-only titles do not become empty keys");
            string variants=Path.Combine(root,"punctuation");Directory.CreateDirectory(variants);
            Audio(Path.Combine(variants,"a.flac"),"440","Дым сигарет, с ментолом");Audio(Path.Combine(variants,"b.flac"),"880","Дым сигарет с ментолом");File.Copy(Path.Combine(variants,"a.flac"),Path.Combine(variants,"exact.flac"));
            var sortedPlan=Engine.Analyze(new Settings{Folder=variants},CancellationToken.None,null);
            Assert(sortedPlan.Operations.Count(o=>o.Copy)==1&&sortedPlan.Operations.Count(o=>o.Resort)==2,"SHA copies and punctuation variants use separate destinations");
            Assert(sortedPlan.Operations.Where(o=>o.Resort).All(o=>System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(o.Destination),@"^[12]_")),"Resorting filenames retain group numbering");
            Assert(!Directory.Exists(Path.Combine(variants,"Resorting"))&&Directory.GetFiles(variants).Length==3,"Resorting preview performs no moves");
            var originals2=sortedPlan.Tracks.ToDictionary(t=>t.Name,t=>Engine.Hash(Path.Combine(variants,t.Name),0,CancellationToken.None));
            try{Engine.Apply(sortedPlan,progress=>{throw new Exception("Simulated resorting failure");});throw new Exception("No rollback failure");}catch(IOException){Assert(originals2.All(x=>File.Exists(Path.Combine(variants,x.Key))&&Engine.Hash(Path.Combine(variants,x.Key),0,CancellationToken.None)==x.Value),"Combined copy and Resorting rollback restores originals");}
            string conflict=sortedPlan.Operations.First(o=>o.Resort).Destination;File.WriteAllText(conflict,"keep-existing");var replanned=Engine.Analyze(new Settings{Folder=variants},CancellationToken.None,null);
            Assert(replanned.Operations.All(o=>!string.Equals(o.Destination,conflict,StringComparison.OrdinalIgnoreCase)),"Existing Resorting names receive collision suffixes");
            Engine.Apply(replanned,null);Assert(File.ReadAllText(conflict)=="keep-existing","Existing Resorting file is not overwritten");
            Assert(Directory.GetFiles(variants).Length==0&&Directory.GetFiles(Path.Combine(variants,"Duplicates")).Length==1&&Directory.GetFiles(Path.Combine(variants,"Resorting")).Length==3,"All numbered survivors are moved, exact copy goes to Duplicates");
            Assert(replanned.Operations.All(o=>Engine.Hash(o.Destination,0,CancellationToken.None)==originals2[o.Track.Name]),"All moved files preserve original bytes");
            string customV=Path.Combine(root,"custom-variants");Directory.CreateDirectory(customV);Audio(Path.Combine(customV,"a.flac"),"440","Раз, два");Audio(Path.Combine(customV,"b.flac"),"880","Раз два");
            var customVPlan=Engine.Analyze(new Settings{Folder=customV,AutoResorting=false,ResortingFolder=Path.Combine(root,"manual-resorting")},CancellationToken.None,null);Engine.Apply(customVPlan,null);
            Assert(Directory.GetFiles(Path.Combine(root,"manual-resorting")).Length==2,"Custom Resorting directory works");
            string off=Path.Combine(root,"variants-off");Directory.CreateDirectory(off);Audio(Path.Combine(off,"a.flac"),"440","Раз, два");Audio(Path.Combine(off,"b.flac"),"880","Раз два");
            var offPlan=Engine.Analyze(new Settings{Folder=off,MoveVariants=false},CancellationToken.None,null);Assert(offPlan.Operations.All(o=>!o.Resort)&&offPlan.Operations.Length==2,"Optional Resorting toggle preserves numbering in source");Engine.Apply(offPlan,null);Assert(Engine.Analyze(new Settings{Folder=off,MoveVariants=false},CancellationToken.None,null).Operations.Length==0,"Numbering without moving remains idempotent");
            foreach(string badFolder in new[]{off,Path.Combine(off,"Duplicates")})try{Engine.Analyze(new Settings{Folder=off,AutoResorting=false,ResortingFolder=badFolder},CancellationToken.None,null);throw new Exception("Accepted invalid Resorting folder");}catch(Exception e){Assert(e.Message.Contains("отличаться"),"Resorting rejects source and Duplicates folders");}
            string exactOnly=Path.Combine(root,"exact-only");Directory.CreateDirectory(exactOnly);Audio(Path.Combine(exactOnly,"a.flac"),"440","Один трек");File.Copy(Path.Combine(exactOnly,"a.flac"),Path.Combine(exactOnly,"b.flac"));var ep=Engine.Analyze(new Settings{Folder=exactOnly},CancellationToken.None,null);Assert(ep.Operations.Count(o=>o.Copy)==1&&!ep.Operations.Any(o=>o.Resort),"Exact copies alone do not create Resorting groups");
            var application=new Application();application.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var ui=new Controller(dir,Path.Combine(root,"test-settings.json"));ui.Window.Show();SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(ui.Window.Dispatcher));ui.Control<TextBox>("Folder").Text=custom;
            Task analyze=ui.Analyze();PumpUntil(()=>analyze.IsCompleted,15000);analyze.GetAwaiter().GetResult();Assert(ui.CurrentPlan!=null&&ui.Table.Items.Count==1,"WPF runs actual analysis");
            Plan kept=ui.CurrentPlan;ui.Control<TextBox>("Folder").Focus();PumpUntil(()=>true,100);Assert(object.ReferenceEquals(kept,ui.CurrentPlan),"Moving focus preserves plan");
            var rows=Enumerable.Range(0,50000).Reverse().Select(i=>new ResultRow{File="Track "+i.ToString("D6"),Action="Оставить",Reason="Test",Result="Test",Kind="keep"}).ToArray();
            var watch=Stopwatch.StartNew();var order=Controller.SortRows(rows,"File",false,false,CancellationToken.None);Assert(order.First().File=="Track 000000"&&order.Last().File=="Track 049999","50,000 rows sorted correctly");Console.WriteLine("Sorting 50000 rows: "+watch.ElapsedMilliseconds+"ms");
            var beat=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};long last=0,worst=0;var clock=Stopwatch.StartNew();beat.Tick+=delegate{long now=clock.ElapsedMilliseconds;if(last!=0)worst=Math.Max(worst,now-last);last=now;};beat.Start();
            ui.Table.ItemsSource=order;Task sort=Task.Run(()=>Controller.SortRows(rows,"File",true,false,CancellationToken.None));PumpUntil(()=>sort.IsCompleted,15000);sort.GetAwaiter().GetResult();PumpUntil(()=>clock.ElapsedMilliseconds>600,2000);beat.Stop();Assert(worst<500,"UI heartbeat remains responsive with 50,000 rows");Console.WriteLine("Worst UI heartbeat gap: "+worst+"ms");
            int visible=0;for(int i=0;i<ui.Table.Items.Count;i++)if(ui.Table.ItemContainerGenerator.ContainerFromIndex(i)!=null)visible++;Assert(visible<100,"DataGrid creates only visible rows");
            ui.Control<TextBox>("ProbeJobs").Text="8";Assert(ui.CurrentPlan==null&&!ui.Control<Button>("Apply").IsEnabled,"Changing options invalidates plan");
            ui.Control<TextBox>("ResortingFolder").Text=Path.Combine(root,"ui-resorting");ui.Control<CheckBox>("AutoResorting").IsChecked=false;
            Assert(ui.Control<System.Windows.Controls.Grid>("ResortingControls").IsEnabled,"Custom Resorting controls enabled");
            ui.Control<CheckBox>("MoveVariants").IsChecked=false;Assert(!ui.Control<System.Windows.Controls.Grid>("ResortingControls").IsEnabled,"Disabling variants disables destination controls");
            ui.Control<CheckBox>("MoveVariants").IsChecked=true;ui.Window.Close();
            var reopened=new Controller(null,Path.Combine(root,"test-settings.json"));Assert(reopened.Control<TextBox>("ResortingFolder").Text==Path.Combine(root,"ui-resorting")&&reopened.Control<CheckBox>("AutoResorting").IsChecked==false&&reopened.Control<CheckBox>("MoveVariants").IsChecked==true,"Resorting settings persist across launches");reopened.Window.Close();application.Shutdown();
            Console.WriteLine("ALL "+checks+" CHECKS PASSED");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex.ToString());Console.Error.WriteLine("Fixtures retained: "+root);return 1;}
    }
}
}
