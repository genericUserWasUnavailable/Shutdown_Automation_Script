using System.Threading;
namespace WinForm_BroadcastListener_REWORK
{
    public partial class Form1 : Form
    {
        private static int f1Pressed = 0;
        public Form1()
        {
            InitializeComponent();

            this.KeyPreview = true;
            this.KeyDown += Form1_KeyDown;
            this.KeyUp += Form1_KeyUp;
        }

        private async void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F1)
            {
                // Only run if we successfully changed 0 → 1
                if (Interlocked.CompareExchange(ref f1Pressed, 1, 0) == 0)
                {                    
                    await Program.HandleKeyPressAsync(e.KeyCode);
                }
            }
        }
        private void Form1_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F1)
            {
                Interlocked.Exchange(ref f1Pressed, 0);
            }
        }
    }
}
