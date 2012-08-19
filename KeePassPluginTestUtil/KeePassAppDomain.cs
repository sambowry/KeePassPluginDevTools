﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Windows.Forms;
using System.Reflection;
using System.Threading;
using System.IO;
using KeePassLib.Serialization;
using KeePassLib.Keys;
using KeePass.UI;

namespace KeePassPluginTestUtil
{
  /// <summary>
  /// Uses <see cref="System.AppDomain"/> to run multiple KeePass instances
  /// in tests.
  /// <remarks>Since KeePass.Program is a static class, KeePass can only
  /// be run once in a test suite. To work around that problem, you can use
  /// an <see cref="System.AppDomain"/> to run KeePass multiple times while
  /// running tests. Use SetData and GetData methods to pass information
  /// between AppDomains.</remarks>
  /// </summary>
  public class KeePassAppDomain : IDisposable
  {
    private AppDomain appDomain;
    private bool isKeePassRunning;

    private const string friendlyName = "KeePass AppDomain {0}"; // {0} is guid
    private const string keepassProcessName = "KeePass";
    private const string keepassExeName = "KeePass.exe";
    private const string configFileName = "KeePass.config.xml";
    private const string dbFileName = "test{0}.kdbx";
    private const string password = "test";

    private const int keepassStartTimeout = 3000; //msec

    /// <summary>
    /// Create a new AppDomain for running a new instance of KeePass
    /// </summary>
    public KeePassAppDomain()
    {
      this.isKeePassRunning = false;
      string guid = Guid.NewGuid().ToString();
      this.appDomain = AppDomain.CreateDomain(
          string.Format(friendlyName, guid),
          AppDomain.CurrentDomain.Evidence,
          AppDomain.CurrentDomain.SetupInformation);
    }

    public void Dispose()
    {
      if (this.appDomain != null && !this.appDomain.IsFinalizingForUnload()) {
        if (isKeePassRunning) {
          this.appDomain.DoCallBack(delegate()
          {
            KeePass.Program.MainForm.Invoke((MethodInvoker)delegate()
            {
              ToolStripMenuItem FileMenu = (ToolStripMenuItem)KeePass.Program
                .MainForm.MainMenu.Items["m_menuFile"];
              ToolStripMenuItem ExitMenuItem = (ToolStripMenuItem)FileMenu
                .DropDownItems["m_menuFileExit"];
              ExitMenuItem.PerformClick();
            });
          });
          while (isKeePassRunning) {
            // TODO may want a timeout here
          }
        }        
        AppDomain.Unload(this.appDomain);
      }
    }

    /// <summary>
    /// Check to see if KeePass has been initialized.
    /// </summary>
    /// <returns>true if KeePass has been initialized.</returns>
    /// <remarks>This is useful because KeePass can only be initialized
    /// (run) once per AppDomain. This will also return true if KeePass
    /// was run in this AppDomain and has been closed</remarks>
    public bool IsKeePassInitalized()
    {
      const string isInitalizedName = "KEPASS_IS_INITALIZED";
      DoCallBack(delegate()
      {
        AppDomain.CurrentDomain.SetData(
            isInitalizedName,
            KeePass.Program.MainForm != null);
      });
      return (bool)GetData(isInitalizedName);
    }       

    /// <summary>
    /// Starts KeePass in the current AppDomain
    /// </summary>
    /// <param name="exitAllFirst">set to true to send exit-all command
    /// and then wait for all other instances of KeePass to stop before
    /// starting the new instance</param>
    /// <param name="debug">set to true to enable the '--debug' 
    /// command line option</param>
    /// <param name="numDbFiles">Number of database file to load</param>
    /// <param name="newConfig">Setting to true copies a new default 
    /// configuration file to the working directory before starting 
    /// a new instance of KeePass</param>
    /// <returns>true if KeePass started successfully</returns>
    public bool StartKeePass(bool exitAllFirst, bool debug, int numDbFiles,
        bool newConfig)
    {
      if (IsKeePassInitalized()) {
        KeePassControl.ShowErrorMessage(
            "KeePass has already been started in this AppDomain" +
            "\n\nKeePass can only be started once per AppDomain");
        return false;
      }

      if (numDbFiles < 0) {
        throw new ArgumentOutOfRangeException("numDbFiles");
      }

      if (exitAllFirst) {
        KeePassControl.ExitAll();
      }

      /* verify directories */

      Assembly assembly = Assembly.GetAssembly(typeof(KeePass.Program));
      string debugDir = Path.GetDirectoryName(assembly.Location);
      // really shouldn't need to check this
      if (!Directory.Exists(debugDir)) {
        KeePassControl.ShowErrorMessage("Debug directory '" +
            debugDir + "' does not exist." +
            "\nIt should be the location of " + keepassExeName);
        return false;
      }

      /* verify files */

      string keepassExeFile = Path.Combine(debugDir, keepassExeName);
      if (!File.Exists(keepassExeFile)) {
        KeePassControl.ShowErrorMessage("KeePass executable file '" +
            keepassExeFile + "' does not exist." +
            "\nPlease make sure it is set up in References and " +
            "'Copy Local' property is set to true" +
            "\nor fix 'keepassExe' in 'KeePassAppDomain.cs'");
        return false;
      }

      /* copy files to working directory */
      if (newConfig) {
        string debugConfigFile = Path.Combine(debugDir,
            configFileName);
        try {
          File.WriteAllText(debugConfigFile,
              Properties.Resources.KeePass_config_xml);
        } catch (Exception ex) {
          KeePassControl.ShowErrorMessage(
              "Error writing config file '" + debugConfigFile +
              "'.\n\n" + ex.Message);
          return false;
        }
      }

      List<string> testDbFiles = new List<string>();
      for (int i = 1; i <= numDbFiles; i++) {
        string testDbFileN = Path.Combine(debugDir,
            string.Format(dbFileName, i));
        try {
          File.WriteAllBytes(testDbFileN,
              Properties.Resources.test_kdbx);
          testDbFiles.Add(testDbFileN);
        } catch (Exception ex) {
          KeePassControl.ShowErrorMessage(
              "Error writing database file '"
              + testDbFileN + "'." +
              "\n\n" + ex.Message);
          return false;
        }
      }

      /* start keepass with test1.kdbx db */
      try {
        List<string> argList = new List<string>();
        if (numDbFiles > 0) {
          argList.Add(testDbFiles[0]);
          argList.Add("-pw:" + password);
        };
        if (debug) {
          argList.Add("--debug");
        }
        argList.Add("--saveplgxcr");

        /* start KeePass in a separate process and then attach to it. */
                
        Thread keepassThread = new Thread((ThreadStart)delegate()
        {
          this.isKeePassRunning = true;
          try {
            Process.Start(Assembly.GetAssembly(typeof(KeePass.Program)).Location,
                string.Join(" ", argList));
            this.appDomain.ExecuteAssembly(
                Assembly.GetAssembly(typeof(KeePass.Program)).Location);
          } finally {
            this.isKeePassRunning = false;
          }
        });
        keepassThread.SetApartmentState(ApartmentState.STA);
        keepassThread.Start();
        
        DoCallBack(delegate()
        {
          while (KeePass.Program.MainForm == null ||
              !KeePass.Program.MainForm.Visible) {
            Thread.Sleep(250);
          }
        });
      } catch (Exception ex) {
        KeePassControl.ShowErrorMessage(
            "An exception occurred while starting KeePass" +
            "\n\n" + ex.ToString());
        return false;
      }

      /* wait for KeyPass to open */
      Stopwatch stopwatch = new Stopwatch();
      stopwatch.Start();
      while ((stopwatch.ElapsedMilliseconds < keepassStartTimeout) &&
          (!IsKeePassInitalized())) {
        Thread.Sleep(250);
      }
      /* wait for file to open if we asked for at least one file */
      if (numDbFiles >= 1) {
        const string isOneFileOpenName = "KEEPASS_IS_ONE_FILE_OPEN";
        bool isOneFileOpen = false;
        while ((stopwatch.ElapsedMilliseconds < keepassStartTimeout) &&
            !isOneFileOpen) {
          DoCallBack(delegate()
          {
            AppDomain.CurrentDomain.SetData(isOneFileOpenName,
            KeePass.Program.MainForm.IsAtLeastOneFileOpen());
          });
          isOneFileOpen = (bool)GetData(isOneFileOpenName);
          if (isOneFileOpen) {
            continue;
          } else {
            Thread.Sleep(250);
          }
        }
      }
      stopwatch.Stop();

      /* verify that program started */
      while (!IsKeePassInitalized()) {
        DialogResult result = KeePassControl.ShowErrorMessage(
            "KeePass did not start within the specified timeout." +
            "\n\nClick OK when KeyPass has started.", true);
        if (result == DialogResult.Cancel) {
          return false;
        }
      }

      /* open additional database files */
      if (testDbFiles.Count > 1) {
        for (int i = 1, cnt = testDbFiles.Count; i < cnt; i++) {
          try {
            IOConnectionInfo ioConnection = new IOConnectionInfo();
            ioConnection.Path = testDbFiles[i];
            CompositeKey compositeKey = new CompositeKey();
            KcpPassword passwordKey = new KcpPassword(password);
            compositeKey.AddUserKey(passwordKey);
            DoCallBack(delegate()
            {
              KeePass.Program.MainForm.Invoke(
                  (MethodInvoker)delegate()
              {
                KeePass.Program.MainForm.OpenDatabase(
                    ioConnection, compositeKey, true);
              });
            });
          } catch (Exception ex) {
            KeePassControl.ShowErrorMessage(
                "An exception occurred while opening additional " +
                "database file" + "\n\n" + ex.Message);
            return false;
          }
        }
      }

      // plug-ins are disabled in config file so that none are loaded
      // automatically re-enable now so that we can get to the plug-in
      // dialog
      KeePass.App.AppPolicy.Current.Plugins = true;

      return true;
    }

    public void LoadPlgx(string plgxPath)
    {
      const string plgxPathName = "KEEPASS_PLGX_PATH";

      SetData(plgxPathName, plgxPath);

      this.appDomain.DoCallBack(delegate()
      {
        string tdPlgxPath =
          (string)AppDomain.CurrentDomain.GetData(plgxPathName);

        KeePass.Program.MainForm.Invoke((MethodInvoker)delegate()
        {
          OnDemandStatusDialog dlgStatus =
            new OnDemandStatusDialog(true, null);
          dlgStatus.StartLogging(tdPlgxPath, false);

          KeePass.Plugins.PlgxPlugin.Load(tdPlgxPath, dlgStatus);

          dlgStatus.EndLogging();
        });
      });
    }

    /// <summary>
    /// <see cref="System.AppDomain.getData"/>
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public object GetData(string name)
    {
      return this.appDomain.GetData(name);
    }

    /// <summary>
    /// <see cref="System.AppDomain.SetData"/>
    /// </summary>
    /// <param name="name"></param>
    /// <param name="data"></param>
    public void SetData(string name, object data)
    {
      this.appDomain.SetData(name, data);
    }

    /// <summary>
    /// Executes callBackDelegate in this AppDomain
    /// </summary>
    /// <param name="callBackDelegate">method to execute</param>
    public void DoCallBack(CrossAppDomainDelegate callBackDelegate)
    {
      this.appDomain.DoCallBack(callBackDelegate);
    }
  }
}
