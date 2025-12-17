namespace StartupController
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.ListView listViewStartup;
        private System.Windows.Forms.Button btnEnable;
        private System.Windows.Forms.Button btnDisable;
        private System.Windows.Forms.Button btnLaunch;
        private System.Windows.Forms.Button btnMoveUp;
        private System.Windows.Forms.Button btnMoveDown;
        private System.Windows.Forms.Button btnSaveOrder;
        private System.Windows.Forms.Button btnViewLogs;
        private System.Windows.Forms.Button btnHelp;
        private System.Windows.Forms.NotifyIcon notifyIcon;
        private System.Windows.Forms.CheckBox chkSilenceNotifications;
        private System.Windows.Forms.CheckBox chkLaunchProgramsOnStartup;
        private System.Windows.Forms.CheckBox chkLaunchToTray;
        private System.Windows.Forms.ColumnHeader columnHeaderName;
        private System.Windows.Forms.ColumnHeader columnHeaderEnabled;
        private System.Windows.Forms.ColumnHeader columnHeaderPath;
        private System.Windows.Forms.ColumnHeader columnHeaderDescription;

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
            components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            listViewStartup = new ListView();
            btnEnable = new Button();
            btnDisable = new Button();
            btnLaunch = new Button();
            btnMoveUp = new Button();
            btnMoveDown = new Button();
            btnSaveOrder = new Button();
            btnViewLogs = new Button();
            btnHelp = new Button();
            chkSilenceNotifications = new CheckBox();
            notifyIcon = new NotifyIcon(components);
            chkLaunchProgramsOnStartup = new CheckBox();
            chkLaunchToTray = new CheckBox();
            SuspendLayout();
            // 
            // listViewStartup Columns
            // 
            columnHeaderName = new ColumnHeader();
            columnHeaderEnabled = new ColumnHeader();
            columnHeaderPath = new ColumnHeader();
            columnHeaderDescription = new ColumnHeader();

            columnHeaderName.Text = "Name";
            columnHeaderName.Width = 140;
            columnHeaderEnabled.Text = "Status";
            columnHeaderEnabled.Width = 80;
            columnHeaderPath.Text = "Path";
            columnHeaderPath.Width = 220;
            columnHeaderDescription.Text = "Description";
            columnHeaderDescription.Width = 170;

            listViewStartup.Columns.AddRange(new ColumnHeader[] {
                columnHeaderName,
                columnHeaderEnabled,
                columnHeaderPath,
                columnHeaderDescription
            });
            // 
            // listViewStartup
            // 
            listViewStartup.FullRowSelect = true;
            listViewStartup.Location = new Point(12, 12);
            listViewStartup.Name = "listViewStartup";
            listViewStartup.Size = new Size(630, 300);
            listViewStartup.TabIndex = 0;
            listViewStartup.UseCompatibleStateImageBehavior = false;
            listViewStartup.View = View.Details;
            // make the listview resize with the form
            listViewStartup.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            
            // 
            // btnEnable
            // 
            btnEnable.Location = new Point(660, 12);
            btnEnable.Name = "btnEnable";
            btnEnable.Size = new Size(120, 32);
            btnEnable.TabIndex = 1;
            btnEnable.Text = "Enable";
            // anchor buttons to the top-right so they follow the right edge but don't resize
            btnEnable.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // 
            // btnDisable
            // 
            btnDisable.Location = new Point(660, 52);
            btnDisable.Name = "btnDisable";
            btnDisable.Size = new Size(120, 32);
            btnDisable.TabIndex = 2;
            btnDisable.Text = "Disable";
            btnDisable.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // 
            // btnLaunch
            // 
            btnLaunch.Location = new Point(660, 92);
            btnLaunch.Name = "btnLaunch";
            btnLaunch.Size = new Size(120, 32);
            btnLaunch.TabIndex = 3;
            btnLaunch.Text = "Launch";
            btnLaunch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // 
            // btnMoveUp
            // 
            btnMoveUp.Location = new Point(660, 132);
            btnMoveUp.Name = "btnMoveUp";
            btnMoveUp.Size = new Size(120, 32);
            btnMoveUp.TabIndex = 4;
            btnMoveUp.Text = "Move Up";
            btnMoveUp.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // 
            // btnMoveDown
            // 
            btnMoveDown.Location = new Point(660, 172);
            btnMoveDown.Name = "btnMoveDown";
            btnMoveDown.Size = new Size(120, 32);
            btnMoveDown.TabIndex = 5;
            btnMoveDown.Text = "Move Down";
            btnMoveDown.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // 
            // btnSaveOrder
            // 
            btnSaveOrder.Location = new Point(660, 212);
            btnSaveOrder.Name = "btnSaveOrder";
            btnSaveOrder.Size = new Size(120, 32);
            btnSaveOrder.TabIndex = 6;
            btnSaveOrder.Text = "Save Order";
            btnSaveOrder.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // 
            // btnViewLogs
            // 
            btnViewLogs.Location = new Point(660, 252);
            btnViewLogs.Name = "btnViewLogs";
            btnViewLogs.Size = new Size(120, 32);
            btnViewLogs.TabIndex = 7;
            btnViewLogs.Text = "View Logs";
            btnViewLogs.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // 
            // btnHelp
            // 
            btnHelp.Location = new Point(660, 292);
            btnHelp.Name = "btnHelp";
            btnHelp.Size = new Size(120, 32);
            btnHelp.TabIndex = 8;
            btnHelp.Text = "Help";
            btnHelp.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            // 
            // chkSilenceNotifications
            // 
            chkSilenceNotifications.AutoSize = true;
            chkSilenceNotifications.Location = new Point(12, 320);
            chkSilenceNotifications.Name = "chkSilenceNotifications";
            chkSilenceNotifications.Size = new Size(134, 19);
            chkSilenceNotifications.TabIndex = 9;
            chkSilenceNotifications.Text = "Silence Notifications";
            chkSilenceNotifications.UseVisualStyleBackColor = true;
            // keep checkboxes anchored to bottom so they stay at the bottom when resizing
            chkSilenceNotifications.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            // 
            // notifyIcon
            // 
            notifyIcon.Icon = (Icon)resources.GetObject("notifyIcon.Icon");
            notifyIcon.Text = "StartupController";
            notifyIcon.Visible = true;
            // 
            // chkLaunchProgramsOnStartup
            // 
            chkLaunchProgramsOnStartup.AutoSize = true;
            chkLaunchProgramsOnStartup.Location = new Point(377, 320);
            chkLaunchProgramsOnStartup.Name = "chkLaunchProgramsOnStartup";
            chkLaunchProgramsOnStartup.Size = new Size(265, 19);
            chkLaunchProgramsOnStartup.TabIndex = 2;
            chkLaunchProgramsOnStartup.Text = "Launch Enabled Programs On System Startup";
            chkLaunchProgramsOnStartup.UseVisualStyleBackColor = true;
            chkLaunchProgramsOnStartup.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            // 
            // chkLaunchToTray
            // 
            chkLaunchToTray.AutoSize = true;
            chkLaunchToTray.Location = new Point(211, 320);
            chkLaunchToTray.Name = "chkLaunchToTray";
            chkLaunchToTray.Size = new Size(106, 19);
            chkLaunchToTray.TabIndex = 2;
            chkLaunchToTray.Text = "Launch To Tray";
            chkLaunchToTray.UseVisualStyleBackColor = true;
            chkLaunchToTray.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 360);
            // allow the form to be resized
            this.MinimumSize = new Size(816, 399);
            this.AutoScaleMode = AutoScaleMode.Font;
            Controls.Add(listViewStartup);
            Controls.Add(btnEnable);
            Controls.Add(btnDisable);
            Controls.Add(btnLaunch);
            Controls.Add(btnMoveUp);
            Controls.Add(btnMoveDown);
            Controls.Add(btnSaveOrder);
            Controls.Add(btnViewLogs);
            Controls.Add(btnHelp);
            Controls.Add(chkSilenceNotifications);
            Controls.Add(chkLaunchProgramsOnStartup);
            Controls.Add(chkLaunchToTray);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "Form1";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "StartupController";
            ResumeLayout(false);
            PerformLayout();

        }

        #endregion



        
    }
}
