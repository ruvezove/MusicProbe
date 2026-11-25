using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;

namespace MusicProbe {
public sealed class Controller {
    public readonly Window Window;
    public readonly DataGrid Table;
    public Plan CurrentPlan {get;private set;}
    public bool Working {get;private set;}
    public bool Sorting {get;private set;}
    bool loading=true, applying;
    Settings saved;
    ResultRow[] allRows=new ResultRow[0];
    CancellationTokenSource cancellation, viewCancellation;
    int viewVersion;
    string sortField="File";
    bool descending;
    ProgressState latest;
    readonly DispatcherTimer progressTimer, saveTimer;
    readonly string Config;
    static readonly string DefaultConfig=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MusicProbe","settings.json");
    public T Control<T>(string name) where T:class {return Window.FindName(name) as T;}
    TextBox Input(string name) {return Control<TextBox>(name);}
    Button Button(string name) {return Control<Button>(name);}
    TextBlock Text(string name) {return Control<TextBlock>(name);}
    CheckBox Check(string name) {return Control<CheckBox>(name);}
    void Status(string text) {Text("Status").Text=text;}
    public Controller(string initialFolder,string configPath=null) {
        Config=configPath??DefaultConfig;
        using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Ui.xaml"))Window=(Window)XamlReader.Load(stream);
        Table=Control<DataGrid>("Files");
        saved=new Settings();string loadError="";
        if(File.Exists(Config))try{saved=new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Config));saved.Validate();}catch(Exception){saved=new Settings();loadError="Сохранённые настройки повреждены. Использованы значения по умолчанию.";}
        if(!string.IsNullOrEmpty(initialFolder))saved.Folder=initialFolder;
        foreach(var f in typeof(Settings).GetFields()) {
            if(f.FieldType==typeof(bool))Check(f.Name).IsChecked=(bool)f.GetValue(saved);
            else Input(f.Name).Text=Convert.ToString(f.GetValue(saved),CultureInfo.InvariantCulture);
        }
        progressTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};
        progressTimer.Tick+=delegate {var p=Interlocked.Exchange(ref latest,null);if(p!=null){Status(p.Text);Control<ProgressBar>("Progress").IsIndeterminate=false;Control<ProgressBar>("Progress").Value=p.Fraction;}};
        saveTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(650)};
        saveTimer.Tick+=delegate {saveTimer.Stop();Persist();};
        foreach(var field in typeof(Settings).GetFields()) {
            if(field.FieldType==typeof(bool)){Check(field.Name).Checked+=Changed;Check(field.Name).Unchecked+=Changed;}
            else Input(field.Name).TextChanged+=Changed;
        }
        Check("AutoCopies").Unchecked+=delegate {
            if(!loading&&Input("CopiesFolder").Text.Trim().Length==0) {
                string folder=FolderPicker.Pick(Window,Input("Folder").Text,"Папка для точных копий");
                if(folder==null)Check("AutoCopies").IsChecked=true;else Input("CopiesFolder").Text=folder;
            }
        };
        Check("AutoResorting").Unchecked+=delegate {
            if(!loading&&Check("MoveVariants").IsChecked==true&&Input("ResortingFolder").Text.Trim().Length==0) {
                string folder=FolderPicker.Pick(Window,Input("Folder").Text,"Папка для разных версий треков");
                if(folder==null)Check("AutoResorting").IsChecked=true;else Input("ResortingFolder").Text=folder;
            }
        };
        Button("BrowseResorting").Click+=delegate {string folder=FolderPicker.Pick(Window,Input("ResortingFolder").Text,"Папка для разных версий треков");if(folder!=null)Input("ResortingFolder").Text=folder;};
        Button("Browse").Click+=delegate {string folder=FolderPicker.Pick(Window,Input("Folder").Text,"Папка с музыкой");if(folder!=null)Input("Folder").Text=folder;};
        Button("BrowseCopies").Click+=delegate {string folder=FolderPicker.Pick(Window,Input("CopiesFolder").Text,"Папка для точных копий");if(folder!=null)Input("CopiesFolder").Text=folder;};
        Button("OpenFolder").Click+=delegate {string p=Input("Folder").Text;if(Directory.Exists(p))try{Process.Start(new ProcessStartInfo("explorer.exe","\""+Path.GetFullPath(p)+"\"") {UseShellExecute=true});}catch(Exception ex){Status(ex.Message);}else Status("Выберите существующую папку.");};
        Button("Analyze").Click+=async delegate {await Analyze();};
        Button("Cancel").Click+=delegate {if(cancellation!=null&&!applying){cancellation.Cancel();Button("Cancel").IsEnabled=false;Status("Останавливаем анализ…");}};
        Button("Apply").Click+=async delegate {await Apply();};
        Table.Sorting+=async delegate(object sender,DataGridSortingEventArgs e){e.Handled=true;string field=e.Column.SortMemberPath;descending=sortField==field?!descending:false;sortField=field;await RefreshRows();};
        Check("ChangesOnly").Checked+=async delegate{await RefreshRows();};Check("ChangesOnly").Unchecked+=async delegate{await RefreshRows();};
        Window.Closing+=delegate(object sender,CancelEventArgs e){if(Working){e.Cancel=true;if(!applying&&cancellation!=null){cancellation.Cancel();Status("Останавливаем анализ. После завершения окно можно закрыть.");}else Status("Дождитесь завершения применения изменений.");}else{Persist();if(viewCancellation!=null)viewCancellation.Cancel();progressTimer.Stop();saveTimer.Stop();}};
        Window.SourceInitialized+=delegate {int dark=1;try{DwmSetWindowAttribute(new WindowInteropHelper(Window).Handle,20,ref dark,4);}catch(EntryPointNotFoundException){} };
        loading=false;UpdateControls();if(loadError.Length>0)Status(loadError);
    }
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int length);
    void Changed(object sender,RoutedEventArgs args) {
        if(loading||Working)return;
        if(CurrentPlan!=null){CurrentPlan=null;allRows=new ResultRow[0];viewVersion++;if(viewCancellation!=null)viewCancellation.Cancel();Table.ItemsSource=null;Control<StackPanel>("Empty").Visibility=Visibility.Visible;Text("Summary").Text="Параметры изменены — выполните анализ заново";}
        UpdateControls();saveTimer.Stop();saveTimer.Start();
    }
    Settings ReadSettings() {
        var result=new Settings();
        foreach(var f in typeof(Settings).GetFields()) {
            if(f.FieldType==typeof(bool))f.SetValue(result,Check(f.Name).IsChecked==true);
            else if(f.FieldType==typeof(string))f.SetValue(result,Input(f.Name).Text.Trim());
            else if(f.FieldType==typeof(int)){int n;if(!int.TryParse(Input(f.Name).Text,out n))throw new Exception("Проверьте числовые параметры обработки.");f.SetValue(result,n);}
            else {double n;if(!double.TryParse(Input(f.Name).Text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out n))throw new Exception("Допуск должен быть числом.");f.SetValue(result,n);}
        }
        result.Validate();if(!result.AutoCopies&&result.CopiesFolder.Length==0)throw new Exception("Выберите папку для копий.");if(result.MoveVariants&&!result.AutoResorting&&result.ResortingFolder.Length==0)throw new Exception("Выберите папку Resorting.");return result;
    }
    void Persist() {
        if(loading)return;
        try{saved=ReadSettings();}catch{saved.Folder=Input("Folder").Text.Trim();}
        string temporary=Config+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(Config));File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(saved),System.Text.Encoding.UTF8);
            if(File.Exists(Config))File.Replace(temporary,Config,null);else File.Move(temporary,Config);
        }catch(Exception ex){Status("Не удалось сохранить настройки: "+ex.Message);if(File.Exists(temporary))File.Delete(temporary);}
    }
    void UpdateControls() {
        Control<Grid>("Inputs").IsEnabled=!Working;
        Control<Grid>("CopiesControls").IsEnabled=Check("AutoCopies").IsChecked!=true;
        Control<Border>("ResortingPanel").IsEnabled=!Working;
        Control<Grid>("ResortingControls").IsEnabled=Check("MoveVariants").IsChecked==true&&Check("AutoResorting").IsChecked!=true;
        Check("AutoResorting").IsEnabled=Check("MoveVariants").IsChecked==true;
        Button("Analyze").IsEnabled=!Working;Button("Cancel").IsEnabled=Working&&!applying;
        Button("Apply").IsEnabled=!Working&&CurrentPlan!=null&&CurrentPlan.Operations.Length>0;
    }
    void SetWorking(bool value) {
        Working=value;UpdateControls();var progress=Control<ProgressBar>("Progress");progress.IsIndeterminate=value;progress.Value=0;
        if(value)progressTimer.Start();else{progressTimer.Stop();Interlocked.Exchange(ref latest,null);progress.IsIndeterminate=false;progress.Value=1;}
    }
    static string Error(Exception ex) {var aggregate=ex as AggregateException;if(aggregate!=null)ex=aggregate.Flatten().InnerExceptions.First();return ex.Message;}
    public async Task Analyze() {
        Settings settings;
        try{settings=ReadSettings();if(settings.Folder.Length==0)throw new Exception("Сначала выберите папку с музыкой.");}catch(Exception ex){Status(ex.Message);return;}
        Persist();if(viewCancellation!=null)viewCancellation.Cancel();viewVersion++;CurrentPlan=null;allRows=new ResultRow[0];Table.ItemsSource=null;Control<StackPanel>("Empty").Visibility=Visibility.Collapsed;
        cancellation=new CancellationTokenSource();SetWorking(true);Status("Чтение папки…");Text("Summary").Text="Анализируем аудиофайлы";
        try {
            var plan=await Task.Run(()=>Engine.Analyze(settings,cancellation.Token,p=>Interlocked.Exchange(ref latest,p)));
            cancellation.Token.ThrowIfCancellationRequested();CurrentPlan=plan;allRows=plan.Rows;await RefreshRows();
            Text("Summary").Text=string.Format("Файлов: {0}   Точных копий: {1}   Переименовать: {2}   Ошибок: {3}   В Resorting: {4}",plan.Tracks.Length,plan.Operations.Count(o=>o.Copy),plan.Operations.Count(o=>!o.Copy&&!o.Resort),plan.Tracks.Count(t=>t.Error.Length>0),plan.Operations.Count(o=>o.Resort));
            Status(plan.Tracks.Length==0?"Аудиофайлы в этой папке не найдены.":string.Format("Анализ завершён за {0:0.0} с. {1}",plan.Seconds,plan.Operations.Length==0?"Изменений нет.":"Проверьте план и нажмите «Применить»."));
        }catch(OperationCanceledException){Status("Анализ отменён. Файлы не изменены.");Text("Summary").Text="Анализ отменён";CurrentPlan=null;}
        catch(Exception ex){Status(Error(ex));Text("Summary").Text="Анализ не завершён";CurrentPlan=null;}
        finally{SetWorking(false);cancellation.Dispose();cancellation=null;}
    }
    public async Task RefreshRows() {
        if(viewCancellation!=null)viewCancellation.Cancel();viewCancellation=new CancellationTokenSource();CancellationToken token=viewCancellation.Token;
        int version=++viewVersion;ResultRow[] source=allRows;bool only=Check("ChangesOnly").IsChecked==true;string field=sortField;bool reverse=descending;Sorting=true;
        try {
            ResultRow[] sorted=await Task.Run(()=>SortRows(source,field,reverse,only,token));
            if(version!=viewVersion)return;Table.ItemsSource=sorted;Control<StackPanel>("Empty").Visibility=source.Length==0?Visibility.Visible:Visibility.Collapsed;
            foreach(var column in Table.Columns)column.SortDirection=column.SortMemberPath==field?(reverse?ListSortDirection.Descending:ListSortDirection.Ascending):(ListSortDirection?)null;
            if(CurrentPlan!=null&&only&&sorted.Length==0)Status("Нет изменений или ошибок для отображения.");
        }catch(OperationCanceledException){}finally{if(version==viewVersion)Sorting=false;}
    }
    public static ResultRow[] SortRows(ResultRow[] source,string field,bool reverse,bool only,CancellationToken token) {
        Func<ResultRow,string> key=r=>r.File;if(field=="Action")key=r=>r.Action;else if(field=="Reason")key=r=>r.Reason;else if(field=="Result")key=r=>r.Result;
        ResultRow[] rows=(only?source.Where(r=>r.Kind!="keep"):source).ToArray();int count=0;
        try { Array.Sort(rows,(a,b)=>{if((++count&1023)==0)token.ThrowIfCancellationRequested();int n=StringComparer.OrdinalIgnoreCase.Compare(key(a),key(b));if(n==0)n=StringComparer.OrdinalIgnoreCase.Compare(a.File,b.File);return reverse?-Math.Sign(n):Math.Sign(n);}); } catch(InvalidOperationException){token.ThrowIfCancellationRequested();throw;}
        token.ThrowIfCancellationRequested();return rows;
    }
    public async Task Apply() {
        var plan=CurrentPlan;if(Working||plan==null||plan.Operations.Length==0)return;
        string message=string.Format("Точных копий в Duplicates: {0}\nРазных версий в Resorting: {3}\nПереименовать на месте: {1}\n\nПапка копий: {2}\nПапка разных версий: {4}\n\nПрименить этот план?",plan.Operations.Count(o=>o.Copy),plan.Operations.Count(o=>!o.Copy&&!o.Resort),plan.CopiesFolder,plan.Operations.Count(o=>o.Resort),plan.ResortingFolder);
        if(MessageBox.Show(Window,message,"MusicProbe",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        applying=true;SetWorking(true);Status("Проверяем файлы перед применением…");
        try{string result=await Task.Run(()=>Engine.Apply(plan,p=>Interlocked.Exchange(ref latest,p)));CurrentPlan=null;allRows=new ResultRow[0];Table.ItemsSource=null;Text("Summary").Text="Изменения применены";Status(result);Control<StackPanel>("Empty").Visibility=Visibility.Collapsed;}
        catch(Exception ex){CurrentPlan=null;Status("Применение не завершено. Выполните анализ заново.");MessageBox.Show(Window,Error(ex),"MusicProbe",MessageBoxButton.OK,MessageBoxImage.Error);}
        finally{applying=false;SetWorking(false);}
    }
}
public static class FolderPicker {
    [ComImport,Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]class FileOpenDialog { }
    [ComImport,Guid("D57C7288-D4AD-4768-BE02-9D969532D960"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IOpen {
        [PreserveSig]int Show(IntPtr owner);
        void SetFileTypes(uint count,IntPtr types);void SetFileTypeIndex(uint index);void GetFileTypeIndex(out uint index);void Advise(IntPtr events,out uint cookie);void Unadvise(uint cookie);
        void SetOptions(uint options);void GetOptions(out uint options);void SetDefaultFolder(IShellItem item);void SetFolder(IShellItem item);void GetFolder(out IShellItem item);void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)]string name);void GetFileName(out IntPtr name);void SetTitle([MarshalAs(UnmanagedType.LPWStr)]string title);void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)]string text);void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)]string text);void GetResult(out IShellItem item);
    }
    [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IShellItem {
        void BindToHandler(IntPtr context,ref Guid handler,ref Guid iid,out IntPtr result);void GetParent(out IShellItem parent);void GetDisplayName(uint kind,out IntPtr name);void GetAttributes(uint mask,out uint attributes);void Compare(IShellItem item,uint hint,out int order);
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]static extern void SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,out IShellItem item);
    public static string Pick(Window owner,string initial,string title) {
        var dialog=(IOpen)new FileOpenDialog();IShellItem item=null;
        try {
            dialog.SetOptions(0x20|0x40|0x800|0x8);dialog.SetTitle(title);
            if(Directory.Exists(initial)){Guid iid=typeof(IShellItem).GUID;SHCreateItemFromParsingName(Path.GetFullPath(initial),IntPtr.Zero,ref iid,out item);dialog.SetFolder(item);Marshal.FinalReleaseComObject(item);item=null;}
            int hr=dialog.Show(new WindowInteropHelper(owner).Handle);if(unchecked((uint)hr)==0x800704C7)return null;if(hr<0)Marshal.ThrowExceptionForHR(hr);
            dialog.GetResult(out item);IntPtr value;item.GetDisplayName(0x80058000,out value);try{return Marshal.PtrToStringUni(value);}finally{Marshal.FreeCoTaskMem(value);}
        }finally{if(item!=null)Marshal.FinalReleaseComObject(item);Marshal.FinalReleaseComObject(dialog);}
    }
}
public static class Program {
    [STAThread]public static void Main(string[] args) {
        try {var app=new Application();var controller=new Controller(args.Length>0?args[0]:null);app.Run(controller.Window);}
        catch(Exception ex){MessageBox.Show(ex.Message,"MusicProbe",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
}
}
