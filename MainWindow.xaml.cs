﻿namespace BotwTrainer
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;

    using BotwTrainer.Properties;

    /// <summary>
    /// Interaction logic for MainWindow
    /// </summary>
    public partial class MainWindow
    {
        // The original list of values that take effect when you save / load
        private const uint SaveItemStart = 0x3FCE7FF0;

        // Technically your first item as they are stored in reverse so we work backwards
        private const uint ItemEnd = 0x43CA2AEC;

        // 0x140 (320) is the amount of items to search for in memory. Over estimating at this point.
        // We start at the end and go back in jumps of 0x220 getting data 320 times
        private const uint ItemStart = ItemEnd - (0x140 * 0x220);

        private const uint CodeHandlerStart = 0x01133000;

        private const uint CodeHandlerEnd = 0x01134300;

        private const uint CodeHandlerEnabled = 0x10014CFC;

        private readonly List<Item> items;

        private bool itemOrderDescending = false;

        private TCPGecko tcpGecko;

        private bool connected;

        public MainWindow()
        {
            this.InitializeComponent();

            IpAddress.Text = Settings.Default.IpAddress;

            this.items = new List<Item>();
        }

        private enum Cheat
        {
            Stamina = 0,
            Health = 1,
            Run = 2,
            Rupees = 3
        }

        private void ConnectClick(object sender, RoutedEventArgs e)
        {
            this.tcpGecko = new TCPGecko(this.IpAddress.Text, 7331);

            try
            {
                this.connected = this.tcpGecko.Connect();

                if (this.connected)
                {
                    Settings.Default.IpAddress = IpAddress.Text;
                    Settings.Default.Save();

                    this.ToggleControls();
                    this.Continue.Visibility = Visibility.Visible;
                }
            }
            catch (ETCPGeckoException ex)
            {
                MessageBox.Show(ex.Message);

                this.connected = false;
            }
            catch (System.Net.Sockets.SocketException)
            {
                MessageBox.Show("Wrong IP");

                this.connected = false;
            }
        }

        private void DisconnectClick(object sender, RoutedEventArgs e)
        {
            try
            {
                this.connected = this.tcpGecko.Disconnect();

                this.ToggleControls();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private async void LoadClick(object sender, RoutedEventArgs e)
        {
            ((Button)sender).IsEnabled = false;
            this.Save.IsEnabled = false;

            this.items.Clear();

            var result = await Task.Run(() => this.LoadDataAsync());

            if (result)
            {
                this.DebugData();

                this.LoadTab(this.Weapons, new[] { 0 });
                this.LoadTab(this.BowsArrows, new[] { 1, 2 });
                this.LoadTab(this.Shields, new[] { 3 });
                this.LoadTab(this.Armour, new[] { 4, 5, 6 });
                this.LoadTab(this.Materials, new[] { 7 });
                this.LoadTab(this.Food, new[] { 8 });
                this.LoadTab(this.KeyItems, new[] { 9 });

                CurrentStamina.Text = this.tcpGecko.peek(0x42439598).ToString(CultureInfo.InvariantCulture);
                CurrentHealth.Text = this.tcpGecko.peek(0x439B6558).ToString(CultureInfo.InvariantCulture);
                CurrentRupees.Text = this.tcpGecko.peek(0x4010AA0C).ToString(CultureInfo.InvariantCulture);

                this.Save.IsEnabled = true;

                ((Button)sender).Content = "Refresh";
                ((Button)sender).IsEnabled = true;

                MessageBox.Show("Data transfer complete");
            }
        }

        private void LoadTab(TabItem tab, IEnumerable<int> pages)
        {
            var panel = new WrapPanel { Name = "PanelContent" };

            var x = 1;

            foreach (var page in pages)
            {
                var thisPage = page;
                var list = this.items.Where(i => i.Page == thisPage).OrderBy(i => i.Address);

                if (this.itemOrderDescending)
                {
                    list = list.OrderByDescending(i => i.Address);
                }

                foreach (var item in list)
                {
                    var label = string.Format("Item {0}:", x);
                    var value = item.Value;
                    if (value > int.MaxValue)
                    {
                        value = 0;
                    }

                    panel.Children.Add(new Label
                                           {
                                               Content = label,
                                               ToolTip = item.Address.ToString("X"),
                                               Width = 55,
                                               Margin = new Thickness(15, 20, 5, 30)
                                           });

                    var isArmour = item.Page == 4 || item.Page == 5 || item.Page == 6;

                    var tb = new TextBox
                                 {
                                     Text = value.ToString(CultureInfo.InvariantCulture),
                                     Width = 60,
                                     Height = 22,
                                     Margin = new Thickness(0, 20, 10, 30),
                                     Name = "Item_" + item.AddressHex,
                                     IsEnabled = !isArmour
                                 };

                    tb.PreviewTextInput += this.NumberValidationTextBox;

                    var check = (TextBox)this.FindName("Item_" + item.AddressHex);
                    if (check != null)
                    {
                        this.UnregisterName("Item_" + item.AddressHex);
                    }

                    this.RegisterName("Item_" + item.AddressHex, tb);

                    panel.Children.Add(tb);

                    x++;
                }
            }

            if (tab.Name == "Materials")
            {
                MaterialsContent.Content = panel;
                return;
            }

            tab.Content = panel;
        }

        private bool LoadDataAsync()
        {
            Dispatcher.Invoke(() => { Continue.Content = "Loading..."; });

            try
            {
                var x = 0;

                uint end = ItemEnd;

                while (end >= ItemStart)
                {
                    // If we start to hit FFFFFFFF then we break as its the end of the items
                    var page = this.tcpGecko.peek(end);
                    if (page > 9)
                    {
                        Dispatcher.Invoke(() => this.UpdateProgress(100));
                        break;
                    }

                    var item = new Item
                    {
                        Address = end,
                        Page = Convert.ToInt32(page),
                        Unknown = Convert.ToInt32(this.tcpGecko.peek(end + 0x4)),
                        Value = this.tcpGecko.peek(end + 0x8),
                        Equipped = this.tcpGecko.peek(end + 0xC),
                        ModAmount = this.tcpGecko.peek(end + 0x5C),
                        ModType = this.tcpGecko.peek(end + 0x64),
                    };

                    this.items.Add(item);

                    Dispatcher.Invoke(() => { Continue.Content = string.Format("Loading...Items found: {0}", x); });

                    var currentPercent = (100m / 320m) * x;
                    Dispatcher.Invoke(() => this.UpdateProgress(Convert.ToInt32(currentPercent)));

                    end -= 0x220;
                    x++;
                }

                return true;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(
                    () =>
                        {
                            Continue.Content = ex.Message;
                            this.connected = false;
                            this.ToggleControls();
                        });
                return false;
            }
        }

        private void UpdateProgress(int percent)
        {
            Progress.Value = percent;
        }

        private void DebugData()
        {
            // Debug Grid data
            DebugGrid.ItemsSource = this.items;

            DebugIntro.Content = string.Format("Showing {0} items", this.items.Count);

            // Show extra info in 'Other' tab to see if our cheats are looking in the correct place
            var stamina1 = this.tcpGecko.peek(0x42439594).ToString("X");
            var stamina2 = this.tcpGecko.peek(0x42439598).ToString("X");
            this.StaminaData.Content = string.Format("[0x42439594 = {0}, 0x42439598 = {1}]", stamina1, stamina2);

            var health = this.tcpGecko.peek(0x439B6558);
            this.HealthData.Content = string.Format("0x439B6558 = {0}", health);

            var run = this.tcpGecko.peek(0x43A88CC4).ToString("X");
            this.RunData.Content = string.Format("0x43A88CC4 = {0}", run);

            var rupee1 = this.tcpGecko.peek(0x3FC92D10);
            var rupee2 = this.tcpGecko.peek(0x4010AA0C);
            var rupee3 = this.tcpGecko.peek(0x40E57E78);
            this.RupeeData.Content = string.Format("[0x3FC92D10 = {0}, 0x4010AA0C = {1}, 0x40E57E78 = {2}]", rupee1, rupee2, rupee3);
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            // Grab the values from the relevant tab and  poke them back to memory
            var tab = (TabItem)TabControl.SelectedItem;

            // For these we amend the 0x3FCE7FF0 area which requires save/load
            if (Equals(tab, this.Weapons) || Equals(tab, this.BowsArrows) || Equals(tab, this.Shields))
            {
                var weaponList = this.items.Where(x => x.Page == 0).ToList();
                var bowList = this.items.Where(x => x.Page == 1).ToList();
                var arrowList = this.items.Where(x => x.Page == 2).ToList();
                var shieldList = this.items.Where(x => x.Page == 3).ToList();

                var y = 0;
                if (Equals(tab, this.Weapons))
                {
                    foreach (var item in weaponList)
                    {
                        var foundTextBox = (TextBox)this.FindName("Item_" + item.AddressHex);
                        if (foundTextBox != null)
                        {
                            var offset = (uint)(SaveItemStart + (y * 0x8));

                            this.tcpGecko.poke32(offset, Convert.ToUInt32(foundTextBox.Text));
                        }

                        y++;
                    }
                }

                if (Equals(tab, this.BowsArrows))
                {
                    // jump past weapons before we start
                    y += weaponList.Count;

                    foreach (var item in bowList)
                    {
                        var foundTextBox = (TextBox)this.FindName("Item_" + item.AddressHex);
                        if (foundTextBox != null)
                        {
                            var offset = (uint)(SaveItemStart + (y * 0x8));

                            this.tcpGecko.poke32(offset, Convert.ToUInt32(foundTextBox.Text));
                        }

                        y++;
                    }
                }

                if (Equals(tab, this.Shields))
                {
                    // jump past weapons/bows/arrows before we start
                    y += weaponList.Count + bowList.Count + arrowList.Count;

                    foreach (var item in shieldList)
                    {
                        var foundTextBox = (TextBox)this.FindName("Item_" + item.AddressHex);
                        if (foundTextBox != null)
                        {
                            var offset = (uint)(SaveItemStart + (y * 0x8));

                            this.tcpGecko.poke32(offset, Convert.ToUInt32(foundTextBox.Text));
                        }

                        y++;
                    }
                }

                MessageBox.Show("Data sent. Please save/load the game.");
            }

            // Here we can poke the values we see in Debug as it has and imemdiate effect
            if (Equals(tab, this.BowsArrows) || Equals(tab, this.Materials) || Equals(tab, this.Food) || Equals(tab, this.KeyItems))
            {
                var page = 0;

                if (Equals(tab, this.BowsArrows))
                {
                    // Just arrows
                    page = 2;
                }

                if (Equals(tab, this.Materials))
                {
                    page = 7;
                }

                if (Equals(tab, this.Food))
                {
                    page = 8;
                }

                if (Equals(tab, this.KeyItems))
                {
                    page = 9;
                }

                foreach (var item in this.items.Where(x => x.Page == page))
                {
                    var foundTextBox = (TextBox)this.FindName("Item_" + item.AddressHex);
                    if (foundTextBox != null)
                    {
                        this.tcpGecko.poke32(item.Address + 0x8, Convert.ToUInt32(foundTextBox.Text));
                    }
                }
            }

            // For the 'Other' tab we mimic JGecko and send cheats to codehandler
            if (Equals(tab, this.Other))
            {
                var selected = new List<Cheat>();

                if (Stamina.IsChecked == true)
                {
                    selected.Add(Cheat.Stamina);
                }

                if (Health.IsChecked == true)
                {
                    selected.Add(Cheat.Health);
                }

                if (Run.IsChecked == true)
                {
                    selected.Add(Cheat.Run);
                }

                if (Rupees.IsChecked == true)
                {
                    selected.Add(Cheat.Rupees);
                }

                this.SetCheats(selected);
            }
        }

        private void SetCheats(List<Cheat> cheats)
        {
            // Disable codehandler before we modify
            this.tcpGecko.poke32(CodeHandlerEnabled, 0x00000000);

            // clear current codes
            var clear = CodeHandlerStart;
            while (clear <= CodeHandlerEnd)
            {
                this.tcpGecko.poke32(clear, 0x0);
                clear += 0x4;
            }

            var codes = new List<uint>();

            // TODO: These are all 32 bit writes so move first and last line of each to loop at the end to avoid duplicating them
            if (cheats.Contains(Cheat.Stamina))
            {
                var value = Convert.ToUInt32(CurrentStamina.Text);

                codes.Add(0x00020000);
                codes.Add(0x42439594);
                codes.Add(value);
                codes.Add(0x00000000);

                codes.Add(0x00020000);
                codes.Add(0x42439598);
                codes.Add(value);
                codes.Add(0x00000000);
            }

            if (cheats.Contains(Cheat.Health))
            {
                var value = Convert.ToUInt32(CurrentHealth.Text);

                codes.Add(0x00020000);
                codes.Add(0x439B6558);
                codes.Add(value);
                codes.Add(0x00000000);
            }

            if (cheats.Contains(Cheat.Run))
            {
                codes.Add(0x00020000);
                codes.Add(0x43A88CC4);
                codes.Add(0x3FC00000);
                codes.Add(0x00000000);
            }

            if (cheats.Contains(Cheat.Rupees))
            {
                var value = Convert.ToUInt32(CurrentRupees.Text);

                codes.Add(0x00020000);
                codes.Add(0x3FC92D10);
                codes.Add(value);
                codes.Add(0x00000000);

                codes.Add(0x00020000);
                codes.Add(0x4010AA0C);
                codes.Add(value);
                codes.Add(0x00000000);

                codes.Add(0x00020000);
                codes.Add(0x40E57E78);
                codes.Add(value);
                codes.Add(0x00000000);
            }

            // Write our selected codes
            var address = CodeHandlerStart;
            foreach (var code in codes)
            {
                this.tcpGecko.poke32(address, code);
                address += 0x4;
            }

            // Re-enable codehandler
            this.tcpGecko.poke32(CodeHandlerEnabled, 0x00000001);
        }

        private void ToggleControls()
        {
            this.IpAddress.IsEnabled = !this.connected;
            this.Connect.IsEnabled = !this.connected;
            this.Disconnect.IsEnabled = this.connected;
            this.TabControl.IsEnabled = this.connected;
            this.Load.IsEnabled = this.connected;

            if (!this.connected)
            {
                Save.IsEnabled = false;
            }
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            var regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }
    }
}
