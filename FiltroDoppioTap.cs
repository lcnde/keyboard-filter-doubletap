// Filtro doppio tap
// -----------------
// Scarta i doppioni della tastiera (key chatter): una seconda pressione dello stesso tasto che arriva
// entro 65 ms dalla pressione precedente. Soglia ricavata dai log del Double Tap Detector sulla G515:
// i doppioni veri arrivano a 43-56 ms, le doppie vere scritte a mano partono da ~70 ms.
//
//  - Non genera mai input: si limita a scartare la pressione doppia e il suo rilascio.
//  - Si spegne del tutto (nessun hook installato) mentre League of Legends e' aperto.
//  - Non tocca il mouse e non filtra Maiusc, Ctrl, Alt e Win.
//  - Non salva il testo: nel log finiscono solo i doppioni scartati (tasto e millisecondi).
//
// Compilazione: compila.cmd (usa il compilatore C# incluso in Windows, .NET Framework 4.x)
//
// Parametri: --soglia 65                    ms massimi tra due pressioni per considerare doppione la seconda
//            --giochi "League of Legends"   processi (separati da ;) durante i quali il filtro si spegne

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Filtro doppio tap")]
[assembly: System.Reflection.AssemblyProduct("Filtro doppio tap")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace FiltroDoppioTap
{
    // Decide evento per evento cosa scartare. Nessuna chiamata a Windows: si testa con tempi simulati.
    public class Filtro
    {
        public double SogliaMs = 65;

        class Stato
        {
            public bool Giu;          // il tasto risulta premuto
            public bool Scartando;    // la pressione in corso e' un doppione scartato
            public bool HaAccettata;
            public double UltimaMs;   // ultima pressione accettata, timer ad alta precisione
            public uint UltimaMsg;    // la stessa, secondo l'orologio dei messaggi di Windows
        }

        readonly Dictionary<int, Stato> stati = new Dictionary<int, Stato>();

        // Maiusc, Ctrl, Alt e Win si tengono premuti: scartarne una ripressione li farebbe risultare rilasciati
        public static bool Modificatore(int vk)
        {
            return (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
        }

        public void Azzera() { stati.Clear(); }

        // true = l'evento va scartato. Per una pressione scartata, intervalloMs = distanza dalla pressione accettata.
        public bool Decidi(int vk, bool giu, double tMs, uint tMsg, out double intervalloMs)
        {
            intervalloMs = -1;
            Stato s;
            if (!stati.TryGetValue(vk, out s)) { s = new Stato(); stati[vk] = s; }

            if (!giu)
            {
                bool scarta = s.Scartando;   // il rilascio del doppione si scarta insieme alla sua pressione
                s.Giu = false;
                s.Scartando = false;
                return scarta;
            }

            if (s.Giu) return s.Scartando;   // ripetizione automatica del tasto tenuto giu'
            s.Giu = true;

            if (s.HaAccettata && !Modificatore(vk))
            {
                double d = tMs - s.UltimaMs;
                uint dMsg = unchecked(tMsg - s.UltimaMsg);
                // Se l'orologio dei messaggi (risoluzione ~16 ms) dice che le pressioni erano lontane, sono
                // arrivate "ammucchiate" per un rallentamento del PC: meglio non scartare niente.
                if (d >= 0 && d < SogliaMs && dMsg <= SogliaMs + 20)
                {
                    s.Scartando = true;
                    intervalloMs = d;
                    return true;
                }
            }
            s.Scartando = false;
            s.HaAccettata = true;
            s.UltimaMs = tMs;
            s.UltimaMsg = tMsg;
            return false;
        }
    }

    public struct Scartato
    {
        public DateTime Ora;
        public int Vk;
        public double IntervalloMs;
    }

    // Hook di tastiera a basso livello su un thread dedicato, installato solo quando il filtro e' attivo
    public static class Gancio
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        const uint WM_QUIT = 0x0012, WM_APPLICA = 0x8001;
        const uint LLKHF_INJECTED = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        public static readonly Filtro Filtro = new Filtro();
        public static readonly ConcurrentQueue<Scartato> Scartati = new ConcurrentQueue<Scartato>();
        public static bool IncludiSimulati;   // solo per i test: filtra anche i tasti inviati dai programmi

        static readonly HookProc proc = Callback;   // riferimento statico: il GC non deve eliminarlo
        static IntPtr hook = IntPtr.Zero;
        static volatile bool richiesto, attivo;
        static uint idThread;
        static Thread thread;

        public static bool Attivo { get { return attivo; } }

        public static void Avvia(bool attivoAllAvvio)
        {
            richiesto = attivoAllAvvio;
            ManualResetEvent pronto = new ManualResetEvent(false);
            thread = new Thread(delegate()
            {
                MSG m;
                PeekMessage(out m, IntPtr.Zero, 0, 0, 0);   // crea la coda dei messaggi del thread
                idThread = GetCurrentThreadId();
                Applica();
                pronto.Set();
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
                    if (m.message == WM_APPLICA) Applica();
                richiesto = false;
                Applica();
            });
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.Highest;
            thread.Start();
            pronto.WaitOne();
        }

        public static void Imposta(bool vuoleAttivo)
        {
            if (richiesto == vuoleAttivo) return;
            richiesto = vuoleAttivo;
            PostThreadMessage(idThread, WM_APPLICA, IntPtr.Zero, IntPtr.Zero);
        }

        public static void Ferma()
        {
            if (thread == null) return;
            PostThreadMessage(idThread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
        }

        static void Applica()
        {
            if (richiesto && hook == IntPtr.Zero)
            {
                Filtro.Azzera();
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            }
            else if (!richiesto && hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
            attivo = hook != IntPtr.Zero;
        }

        static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                int msg = wParam.ToInt32();
                bool giu = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool su = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                if ((giu || su) && ((k.flags & LLKHF_INJECTED) == 0 || IncludiSimulati))
                {
                    double t = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                    double intervallo;
                    if (Filtro.Decidi((int)k.vkCode, giu, t, k.time, out intervallo))
                    {
                        if (intervallo >= 0)
                        {
                            Scartato s;
                            s.Ora = DateTime.Now; s.Vk = (int)k.vkCode; s.IntervalloMs = intervallo;
                            Scartati.Enqueue(s);
                        }
                        return (IntPtr)1;   // scartato: nessun programma lo riceve
                    }
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }
    }

    // Icona nell'area di notifica: stato, conteggio dei doppioni scartati, pausa, log
    class App : ApplicationContext
    {
        readonly NotifyIcon icona = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly ToolStripMenuItem voceAttivo;
        readonly string[] giochi;
        readonly string fileLog;
        readonly string sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
        readonly StringBuilder daScrivere = new StringBuilder();
        readonly Icon iconaAttiva, iconaSpenta;
        readonly DateTime esciAlle;
        bool pausaManuale, inGioco;
        long scartatiSessione;

        public App(string[] giochi, string cartella, double esciDopoSec)
        {
            this.giochi = giochi;
            fileLog = Path.Combine(cartella, "log", "doppioni-scartati.csv");
            esciAlle = esciDopoSec > 0 ? DateTime.Now.AddSeconds(esciDopoSec) : DateTime.MaxValue;
            iconaAttiva = DisegnaIcona(Color.FromArgb(46, 160, 67));
            iconaSpenta = DisegnaIcona(Color.FromArgb(130, 130, 130));

            ContextMenuStrip menu = new ContextMenuStrip();
            voceAttivo = new ToolStripMenuItem("Filtro attivo", null, delegate { pausaManuale = !pausaManuale; Aggiorna(); });
            menu.Items.Add(voceAttivo);
            menu.Items.Add("Apri il log dei doppioni scartati", null, delegate { ApriLog(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Esci", null, delegate { Esci(); });
            icona.ContextMenuStrip = menu;
            icona.Icon = iconaSpenta;
            icona.Visible = true;

            inGioco = GiocoAperto();
            Gancio.Avvia(!inGioco);
            Aggiorna();
            timer.Interval = 1000;
            timer.Tick += delegate { Aggiorna(); if (DateTime.Now >= esciAlle) Esci(); };
            timer.Start();
        }

        bool GiocoAperto()
        {
            foreach (string g in giochi)
            {
                Process[] p = Process.GetProcessesByName(g);
                bool aperto = p.Length > 0;
                foreach (Process x in p) x.Dispose();
                if (aperto) return true;
            }
            return false;
        }

        void Aggiorna()
        {
            inGioco = GiocoAperto();
            bool vuoleAttivo = !pausaManuale && !inGioco;
            Gancio.Imposta(vuoleAttivo);
            RaccogliScartati();

            string stato = pausaManuale ? "in pausa" : inGioco ? "spento (gioco aperto)" : "attivo";
            voceAttivo.Checked = !pausaManuale;
            icona.Icon = vuoleAttivo ? iconaAttiva : iconaSpenta;
            string testo = "Filtro doppio tap: " + stato + "\nDoppioni scartati: " + scartatiSessione;
            icona.Text = testo.Length > 63 ? testo.Substring(0, 63) : testo;
        }

        void RaccogliScartati()
        {
            Scartato s;
            while (Gancio.Scartati.TryDequeue(out s))
            {
                scartatiSessione++;
                daScrivere.Append(s.Ora.ToString("yyyy-MM-dd")).Append(sep)
                          .Append(s.Ora.ToString("HH:mm:ss.fff")).Append(sep)
                          .Append(((Keys)s.Vk).ToString()).Append(sep)
                          .Append(s.IntervalloMs.ToString("0.0")).Append("\r\n");
            }
            if (daScrivere.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fileLog));
                UTF8Encoding utf8 = new UTF8Encoding(true);   // con BOM, cosi' Excel legge bene gli accenti
                if (!File.Exists(fileLog))
                    File.WriteAllText(fileLog, "Data" + sep + "Ora" + sep + "Tasto" + sep + "Tra le pressioni (ms)\r\n", utf8);
                File.AppendAllText(fileLog, daScrivere.ToString(), utf8);
                daScrivere.Length = 0;
            }
            catch { }   // file aperto in un altro programma (es. Excel): si riprova al giro dopo
        }

        void ApriLog()
        {
            RaccogliScartati();
            if (File.Exists(fileLog)) Process.Start(fileLog);
            else MessageBox.Show("Nessun doppione scartato finora.", "Filtro doppio tap");
        }

        void Esci()
        {
            timer.Stop();
            RaccogliScartati();
            icona.Visible = false;
            ExitThread();
        }

        static Icon DisegnaIcona(Color colore)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (GraphicsPath percorso = new GraphicsPath())
                using (SolidBrush sfondo = new SolidBrush(colore))
                using (Font carattere = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat formato = new StringFormat())
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    const int d = 12;
                    percorso.AddArc(1, 1, d, d, 180, 90);
                    percorso.AddArc(30 - d, 1, d, d, 270, 90);
                    percorso.AddArc(30 - d, 30 - d, d, d, 0, 90);
                    percorso.AddArc(1, 30 - d, d, d, 90, 90);
                    percorso.CloseFigure();
                    g.FillPath(sfondo, percorso);
                    formato.Alignment = StringAlignment.Center;
                    formato.LineAlignment = StringAlignment.Center;
                    g.DrawString("1x", carattere, Brushes.White, new RectangleF(0, 1, 32, 32), formato);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    static class Programma
    {
        [STAThread]
        static int Main(string[] args)
        {
            double soglia = 65, esciDopo = 0;
            List<string> giochi = new List<string>(new string[] { "League of Legends" });
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--soglia" && i + 1 < args.Length) soglia = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--giochi" && i + 1 < args.Length) giochi = new List<string>(args[++i].Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
                else if (a == "--includi-simulati") Gancio.IncludiSimulati = true;                                        // solo per i test
                else if (a == "--esci-dopo" && i + 1 < args.Length) esciDopo = double.Parse(args[++i], CultureInfo.InvariantCulture);   // solo per i test
            }

            bool primaIstanza;
            using (Mutex mutex = new Mutex(true, "Local\\FiltroDoppioTap", out primaIstanza))
            {
                if (!primaIstanza)
                {
                    MessageBox.Show("Il filtro doppio tap è già attivo: trovi l'icona verde \"1x\" nell'area di notifica.", "Filtro doppio tap");
                    return 0;
                }
                Gancio.Filtro.SogliaMs = soglia;
                Application.EnableVisualStyles();
                Application.Run(new App(giochi.ToArray(), AppDomain.CurrentDomain.BaseDirectory, esciDopo));
                Gancio.Ferma();
            }
            return 0;
        }
    }
}
