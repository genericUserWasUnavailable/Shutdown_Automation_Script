namespace WinForm_BroadcastListener_REWORK
{
    public class StatusForm : Form 
    {
        public Label StatusLabel = new Label();
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
        public void SetStatus(string text)
        {
            if (InvokeRequired)
                BeginInvoke(new Action(() => StatusLabel.Text = text));
            else
                StatusLabel.Text = text;
        }

        public StatusForm()
        {
            Width = 400;
            Height = 120;
            Text = "Working...";
            StartPosition = FormStartPosition.CenterScreen;

            StatusLabel.Left = 20;
            StatusLabel.Top = 20;
            StatusLabel.Width = 350;
            StatusLabel.Text = "Initializing...";

            Controls.Add(StatusLabel);
        }
    }
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }       

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            SuspendLayout();
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Name = "Form1";
            Text = "Form1";
            Load += Form1_Load;
            ResumeLayout(false);
        }
        private void Form1_Load(object sender, EventArgs e)
        {
            // This method runs when the form loads.
            // You can put initialization logic here.
        }


        #endregion
    }
}
