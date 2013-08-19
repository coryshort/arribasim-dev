/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using OpenMetaverse;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using pCampBot.Interfaces;

namespace pCampBot
{
    /// <summary>
    /// Thread/Bot manager for the application
    /// </summary>
    public class BotManager
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const int DefaultLoginDelay = 5000;

        /// <summary>
        /// Is pCampbot in the process of disconnecting bots?
        /// </summary>
        public bool DisconnectingBots { get; private set; }

        /// <summary>
        /// Delay between logins of multiple bots.
        /// </summary>
        /// <remarks>TODO: This value needs to be configurable by a command line argument.</remarks>
        public int LoginDelay { get; set; }

        /// <summary>
        /// Command console
        /// </summary>
        protected CommandConsole m_console;

        /// <summary>
        /// Controls whether bots start out sending agent updates on connection.
        /// </summary>
        public bool InitBotSendAgentUpdates { get; set; }

        /// <summary>
        /// Controls whether bots request textures for the object information they receive
        /// </summary>
        public bool InitBotRequestObjectTextures { get; set; }

        /// <summary>
        /// Created bots, whether active or inactive.
        /// </summary>
        protected List<Bot> m_bots;

        /// <summary>
        /// Random number generator.
        /// </summary>
        public Random Rng { get; private set; }

        /// <summary>
        /// Track the assets we have and have not received so we don't endlessly repeat requests.
        /// </summary>
        public Dictionary<UUID, bool> AssetsReceived { get; private set; }

        /// <summary>
        /// The regions that we know about.
        /// </summary>
        public Dictionary<ulong, GridRegion> RegionsKnown { get; private set; }

        /// <summary>
        /// First name for bots
        /// </summary>
        private string m_firstName;

        /// <summary>
        /// Last name stem for bots
        /// </summary>
        private string m_lastNameStem;

        /// <summary>
        /// Password for bots
        /// </summary>
        private string m_password;

        /// <summary>
        /// Login URI for bots.
        /// </summary>
        private string m_loginUri;

        /// <summary>
        /// Start location for bots.
        /// </summary>
        private string m_startUri;

        /// <summary>
        /// Postfix bot number at which bot sequence starts.
        /// </summary>
        private int m_fromBotNumber;

        /// <summary>
        /// Wear setting for bots.
        /// </summary>
        private string m_wearSetting;

        /// <summary>
        /// Behaviour switches for bots.
        /// </summary>
        private HashSet<string> m_behaviourSwitches = new HashSet<string>();

        /// <summary>
        /// Constructor Creates MainConsole.Instance to take commands and provide the place to write data
        /// </summary>
        public BotManager()
        {
            InitBotSendAgentUpdates = true;
            InitBotRequestObjectTextures = true;

            LoginDelay = DefaultLoginDelay;

            Rng = new Random(Environment.TickCount);
            AssetsReceived = new Dictionary<UUID, bool>();
            RegionsKnown = new Dictionary<ulong, GridRegion>();

            m_console = CreateConsole();
            MainConsole.Instance = m_console;

            // Make log4net see the console
            //
            ILoggerRepository repository = LogManager.GetRepository();
            IAppender[] appenders = repository.GetAppenders();
            OpenSimAppender consoleAppender = null;

            foreach (IAppender appender in appenders)
            {
                if (appender.Name == "Console")
                {
                    consoleAppender = (OpenSimAppender)appender;
                    consoleAppender.Console = m_console;
                    break;
                }
            }

            m_console.Commands.AddCommand(
                "bot", false, "shutdown", "shutdown", "Shutdown bots and exit", HandleShutdown);

            m_console.Commands.AddCommand(
                "bot", false, "quit", "quit", "Shutdown bots and exit", HandleShutdown);

            m_console.Commands.AddCommand(
                "bot", false, "disconnect", "disconnect [<n>]", "Disconnect bots",
                "Disconnecting bots will interupt any bot connection process, including connection on startup.\n"
                    + "If an <n> is given, then the last <n> connected bots by postfix number are disconnected.\n"
                    + "If no <n> is given, then all currently connected bots are disconnected.",
                HandleDisconnect);

            m_console.Commands.AddCommand(
                "bot", false, "show regions", "show regions", "Show regions known to bots", HandleShowRegions);

            m_console.Commands.AddCommand(
                "bot", false, "show bots", "show bots", "Shows the status of all bots", HandleShowStatus);

//            m_console.Commands.AddCommand("bot", false, "add bots",
//                    "add bots <number>",
//                    "Add more bots", HandleAddBots);

            m_bots = new List<Bot>();
        }

        /// <summary>
        /// Startup number of bots specified in the starting arguments
        /// </summary>
        /// <param name="botcount">How many bots to start up</param>
        /// <param name="cs">The configuration for the bots to use</param>
        public void dobotStartup(int botcount, IConfig startupConfig)
        {
            m_firstName = startupConfig.GetString("firstname");
            m_lastNameStem = startupConfig.GetString("lastname");
            m_password = startupConfig.GetString("password");
            m_loginUri = startupConfig.GetString("loginuri");
            m_fromBotNumber = startupConfig.GetInt("from", 0);
            m_wearSetting = startupConfig.GetString("wear", "no");

            m_startUri = ParseInputStartLocationToUri(startupConfig.GetString("start", "last"));

            Array.ForEach<string>(
                startupConfig.GetString("behaviours", "p").Split(new char[] { ',' }), b => m_behaviourSwitches.Add(b));

            ConnectBots(
                botcount, m_firstName, m_lastNameStem, m_password, m_loginUri, m_startUri, m_fromBotNumber, m_wearSetting, m_behaviourSwitches);
        }

        private void ConnectBots(
            int botcount, string firstName, string lastNameStem, string password, string loginUri, string startUri, int fromBotNumber, string wearSetting,
            HashSet<string> behaviourSwitches)
        {
            MainConsole.Instance.OutputFormat(
                "[BOT MANAGER]: Starting {0} bots connecting to {1}, location {2}, named {3} {4}_<n>",
                botcount,
                loginUri,
                startUri,
                firstName,
                lastNameStem);

            MainConsole.Instance.OutputFormat("[BOT MANAGER]: Delay between logins is {0}ms", LoginDelay);
            MainConsole.Instance.OutputFormat("[BOT MANAGER]: BotsSendAgentUpdates is {0}", InitBotSendAgentUpdates);
            MainConsole.Instance.OutputFormat("[BOT MANAGER]: InitBotRequestObjectTextures is {0}", InitBotRequestObjectTextures);

            for (int i = 0; i < botcount; i++)
            {
                lock (m_bots)
                {
                    if (DisconnectingBots)
                    {
                        MainConsole.Instance.Output(
                            "[BOT MANAGER]: Aborting bot connection due to user-initiated disconnection");
                        break;
                    }

                    string lastName = string.Format("{0}_{1}", lastNameStem, i + fromBotNumber);

                    // We must give each bot its own list of instantiated behaviours since they store state.
                    List<IBehaviour> behaviours = new List<IBehaviour>();
        
                    // Hard-coded for now        
                    if (behaviourSwitches.Contains("c"))
                        behaviours.Add(new CrossBehaviour());

                    if (behaviourSwitches.Contains("g"))
                        behaviours.Add(new GrabbingBehaviour());

                    if (behaviourSwitches.Contains("n"))
                        behaviours.Add(new NoneBehaviour());

                    if (behaviourSwitches.Contains("p"))
                        behaviours.Add(new PhysicsBehaviour());
        
                    if (behaviourSwitches.Contains("t"))
                        behaviours.Add(new TeleportBehaviour());

                    StartBot(this, behaviours, firstName, lastName, password, loginUri, startUri, wearSetting);
                }

                // Stagger logins
                Thread.Sleep(LoginDelay);
            }
        }

        /// <summary>
        /// Parses the command line start location to a start string/uri that the login mechanism will recognize.
        /// </summary>
        /// <returns>
        /// The input start location to URI.
        /// </returns>
        /// <param name='startLocation'>
        /// Start location.
        /// </param>
        private string ParseInputStartLocationToUri(string startLocation)
        {
            if (startLocation == "home" || startLocation == "last")
                return startLocation;

            string regionName;

            // Just a region name or only one (!) extra component.  Like a viewer, we will stick 128/128/0 on the end
            Vector3 startPos = new Vector3(128, 128, 0);

            string[] startLocationComponents = startLocation.Split('/');

            regionName = startLocationComponents[0];

            if (startLocationComponents.Length >= 2)
            {
                float.TryParse(startLocationComponents[1], out startPos.X);

                if (startLocationComponents.Length >= 3)
                {
                    float.TryParse(startLocationComponents[2], out startPos.Y);

                    if (startLocationComponents.Length >= 4)
                        float.TryParse(startLocationComponents[3], out startPos.Z);
                }
            }

            return string.Format("uri:{0}&{1}&{2}&{3}", regionName, startPos.X, startPos.Y, startPos.Z);
        }

//        /// <summary>
//        /// Add additional bots (and threads) to our bot pool
//        /// </summary>
//        /// <param name="botcount">How Many of them to add</param>
//        public void addbots(int botcount)
//        {
//            int len = m_td.Length;
//            Thread[] m_td2 = new Thread[len + botcount];
//            for (int i = 0; i < len; i++)
//            {
//                m_td2[i] = m_td[i];
//            }
//            m_td = m_td2;
//            int newlen = len + botcount;
//            for (int i = len; i < newlen; i++)
//            {
//                startupBot(Config);
//            }
//        }

        /// <summary>
        /// This starts up the bot and stores the thread for the bot in the thread array
        /// </summary>
        /// <param name="bm"></param>
        /// <param name="behaviours">Behaviours for this bot to perform.</param>
        /// <param name="firstName">First name</param>
        /// <param name="lastName">Last name</param>
        /// <param name="password">Password</param>
        /// <param name="loginUri">Login URI</param>
        /// <param name="startLocation">Location to start the bot.  Can be "last", "home" or a specific sim name.</param>
        /// <param name="wearSetting"></param>
        public void StartBot(
             BotManager bm, List<IBehaviour> behaviours,
             string firstName, string lastName, string password, string loginUri, string startLocation, string wearSetting)
        {
            MainConsole.Instance.OutputFormat(
                "[BOT MANAGER]: Starting bot {0} {1}, behaviours are {2}",
                firstName, lastName, string.Join(",", behaviours.ConvertAll<string>(b => b.Name).ToArray()));

            Bot pb = new Bot(bm, behaviours, firstName, lastName, password, startLocation, loginUri);
            pb.wear = wearSetting;
            pb.Client.Settings.SEND_AGENT_UPDATES = InitBotSendAgentUpdates;
            pb.RequestObjectTextures = InitBotRequestObjectTextures;

            pb.OnConnected += handlebotEvent;
            pb.OnDisconnected += handlebotEvent;

            m_bots.Add(pb);

            Thread pbThread = new Thread(pb.startup);
            pbThread.Name = pb.Name;
            pbThread.IsBackground = true;

            pbThread.Start();
        }

        /// <summary>
        /// High level connnected/disconnected events so we can keep track of our threads by proxy
        /// </summary>
        /// <param name="callbot"></param>
        /// <param name="eventt"></param>
        private void handlebotEvent(Bot callbot, EventType eventt)
        {
            switch (eventt)
            {
                case EventType.CONNECTED:
                {
                    m_log.Info("[" + callbot.FirstName + " " + callbot.LastName + "]: Connected");
                    break;
                }

                case EventType.DISCONNECTED:
                {
                    m_log.Info("[" + callbot.FirstName + " " + callbot.LastName + "]: Disconnected");
                    break;
                }
            }
        }

        /// <summary>
        /// Standard CreateConsole routine
        /// </summary>
        /// <returns></returns>
        protected CommandConsole CreateConsole()
        {
            return new LocalConsole("pCampbot");
        }

        private void HandleDisconnect(string module, string[] cmd)
        {
            lock (m_bots)
            {
                int botsToDisconnect;
                int connectedBots = m_bots.Count(b => b.ConnectionState == ConnectionState.Connected);

                if (cmd.Length == 1)
                {
                    botsToDisconnect = connectedBots;
                }
                else
                {
                    if (!ConsoleUtil.TryParseConsoleNaturalInt(MainConsole.Instance, cmd[1], out botsToDisconnect))
                        return;

                    botsToDisconnect = Math.Min(botsToDisconnect, connectedBots);
                }

                DisconnectingBots = true;

                MainConsole.Instance.OutputFormat("Disconnecting {0} bots", botsToDisconnect);

                int disconnectedBots = 0;

                for (int i = m_bots.Count - 1; i >= 0; i--)
                {
                    if (disconnectedBots >= botsToDisconnect)
                        break;

                    Bot thisBot = m_bots[i];

                    if (thisBot.ConnectionState == ConnectionState.Connected)
                    {
                        Util.FireAndForget(o => thisBot.shutdown());
                        disconnectedBots++;
                    }
                }
            }
        }

        private void HandleShutdown(string module, string[] cmd)
        {
            lock (m_bots)
            {
                int connectedBots = m_bots.Count(b => b.ConnectionState == ConnectionState.Connected);

                if (connectedBots > 0)
                {
                    MainConsole.Instance.OutputFormat("Please disconnect {0} connected bots first", connectedBots);
                    return;
                }
            }

            MainConsole.Instance.Output("Shutting down");

            Environment.Exit(0);
        }

        private void HandleShowRegions(string module, string[] cmd)
        {
            string outputFormat = "{0,-30}  {1, -20}  {2, -5}  {3, -5}";
            MainConsole.Instance.OutputFormat(outputFormat, "Name", "Handle", "X", "Y");

            lock (RegionsKnown)
            {
                foreach (GridRegion region in RegionsKnown.Values)
                {
                    MainConsole.Instance.OutputFormat(
                        outputFormat, region.Name, region.RegionHandle, region.X, region.Y);
                }
            }
        }

        private void HandleShowStatus(string module, string[] cmd)
        {
            ConsoleDisplayTable cdt = new ConsoleDisplayTable();
            cdt.AddColumn("Name", 30);
            cdt.AddColumn("Region", 30);
            cdt.AddColumn("Status", 14);
            cdt.AddColumn("Connections", 11);

            Dictionary<ConnectionState, int> totals = new Dictionary<ConnectionState, int>();
            foreach (object o in Enum.GetValues(typeof(ConnectionState)))
                totals[(ConnectionState)o] = 0;

            lock (m_bots)
            {
                foreach (Bot pb in m_bots)
                {
                    Simulator currentSim = pb.Client.Network.CurrentSim;
                    totals[pb.ConnectionState]++;

                    cdt.AddRow(
                        pb.Name, currentSim != null ? currentSim.Name : "(none)", pb.ConnectionState, pb.ConnectionsCount);
                }
            }

            MainConsole.Instance.Output(cdt.ToString());

            ConsoleDisplayList cdl = new ConsoleDisplayList();

            foreach (KeyValuePair<ConnectionState, int> kvp in totals)
                cdl.AddRow(kvp.Key, kvp.Value);

            MainConsole.Instance.Output(cdl.ToString());
        }

        /*
        private void HandleQuit(string module, string[] cmd)
        {
            m_console.Warn("DANGER", "This should only be used to quit the program if you've already used the shutdown command and the program hasn't quit");
            Environment.Exit(0);
        }
        */
//
//        private void HandleAddBots(string module, string[] cmd)
//        {
//            int newbots = 0;
//            
//            if (cmd.Length > 2)
//            {
//                Int32.TryParse(cmd[2], out newbots);
//            }
//            if (newbots > 0)
//                addbots(newbots);
//        }

        internal void Grid_GridRegion(object o, GridRegionEventArgs args)
        {
            lock (RegionsKnown)
            {
                GridRegion newRegion = args.Region;

                if (RegionsKnown.ContainsKey(newRegion.RegionHandle))
                {
                    return;
                }
                else
                {
                    m_log.DebugFormat(
                        "[BOT MANAGER]: Adding {0} {1} to known regions", newRegion.Name, newRegion.RegionHandle);
                    RegionsKnown[newRegion.RegionHandle] = newRegion;
                }
            }
        }
    }
}
