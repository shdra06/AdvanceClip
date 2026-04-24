using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MicaWPF.Controls;

namespace AdvanceClip.Windows
{
    public partial class EmojiPickerWindow : MicaWindow
    {
        public class EmojiItem { public string Emoji { get; set; } = ""; public string Name { get; set; } = ""; public string Category { get; set; } = ""; }

        private List<EmojiItem> _allEmojis = new();
        private string _currentCategory = "😊 Smileys";

        public EmojiPickerWindow()
        {
            InitializeComponent();
            LoadEmojis();
            BuildCategoryTabs();
            FilterEmojis();
        }

        private void LoadEmojis()
        {
            var cats = new Dictionary<string, (string label, string[] emojis)>
            {
                ["😊 Smileys"] = ("😊 Smileys", new[] {
                    "😀","😃","😄","😁","😆","😅","🤣","😂","🙂","🙃","😉","😊","😇","🥰","😍","🤩","😘","😗","😚","😙",
                    "🥲","😋","😛","😜","🤪","😝","🤑","🤗","🤭","🫢","🫣","🤫","🤔","🫡","🤐","🤨","😐","😑","😶","🫥",
                    "😏","😒","🙄","😬","🤥","😌","😔","😪","🤤","😴","😷","🤒","🤕","🤢","🤮","🥵","🥶","🥴","😵","🤯",
                    "🤠","🥳","🥸","😎","🤓","🧐","😕","🫤","😟","🙁","☹️","😮","😯","😲","😳","🥺","🥹","😦","😧","😨",
                    "😰","😥","😢","😭","😱","😖","😣","😞","😓","😩","😫","🥱","😤","😡","😠","🤬","😈","👿","💀","☠️",
                    "💩","🤡","👹","👺","👻","👽","👾","🤖","😺","😸","😹","😻","😼","😽","🙀","😿","😾"
                }),
                ["👋 Hands"] = ("👋 Hands", new[] {
                    "👋","🤚","🖐️","✋","🖖","🫱","🫲","🫳","🫴","👌","🤌","🤏","✌️","🤞","🫰","🤟","🤘","🤙","👈","👉",
                    "👆","🖕","👇","☝️","🫵","👍","👎","✊","👊","🤛","🤜","👏","🙌","🫶","👐","🤲","🤝","🙏","✍️","💅",
                    "🤳","💪","🦾","🦿","🦵","🦶","👂","🦻","👃","🧠","🫀","🫁","🦷","🦴","👀","👁️","👅","👄","🫦"
                }),
                ["👤 People"] = ("👤 People", new[] {
                    "👶","🧒","👦","👧","🧑","👱","👨","🧔","👩","🧓","👴","👵","🙍","🙎","🙅","🙆","💁","🙋","🧏","🙇",
                    "🤦","🤷","👮","🕵️","💂","🥷","👷","🫅","🤴","👸","👳","👲","🧕","🤵","👰","🤰","🫃","🫄","🤱","👼",
                    "🎅","🤶","🦸","🦹","🧙","🧚","🧛","🧜","🧝","🧞","🧟","🧌","💆","💇","🚶","🧍","🧎","🏃","💃","🕺"
                }),
                ["❤️ Hearts"] = ("❤️ Hearts", new[] {
                    "❤️","🧡","💛","💚","💙","💜","🖤","🤍","🤎","💔","❤️‍🔥","❤️‍🩹","❣️","💕","💞","💓","💗","💖","💘","💝",
                    "💟","♥️","💋","💌","💐","🌹","🥀","🌺","🌻","🌼","🌷","🪷","🌸","💮","🏵️","🪻"
                }),
                ["🐶 Animals"] = ("🐶 Animals", new[] {
                    "🐶","🐱","🐭","🐹","🐰","🦊","🐻","🐼","🐻‍❄️","🐨","🐯","🦁","🐮","🐷","🐽","🐸","🐵","🙈","🙉","🙊",
                    "🐒","🐔","🐧","🐦","🐤","🐣","🐥","🦆","🦅","🦉","🦇","🐺","🐗","🐴","🦄","🫎","🐝","🪱","🐛","🦋",
                    "🐌","🐞","🐜","🪰","🪲","🪳","🦟","🦗","🕷️","🦂","🐢","🐍","🦎","🦖","🦕","🐙","🦑","🦐","🦞","🦀",
                    "🐡","🐠","🐟","🐬","🐳","🐋","🦈","🪸","🐊","🐅","🐆","🦓","🫏","🦍","🦧","🐘","🦛","🦏","🐪","🐫"
                }),
                ["🍕 Food"] = ("🍕 Food", new[] {
                    "🍇","🍈","🍉","🍊","🍋","🍌","🍍","🥭","🍎","🍏","🍐","🍑","🍒","🍓","🫐","🥝","🍅","🫒","🥥","🥑",
                    "🍆","🥔","🥕","🌽","🌶️","🫑","🥒","🥬","🥦","🧄","🧅","🍄","🥜","🫘","🌰","🍞","🥐","🥖","🫓","🥨",
                    "🥯","🥞","🧇","🧀","🍖","🍗","🥩","🥓","🍔","🍟","🍕","🌭","🥪","🌮","🌯","🫔","🥙","🧆","🥚","🍳",
                    "🥘","🍲","🫕","🥣","🥗","🍿","🧈","🧂","🥫","🍱","🍘","🍙","🍚","🍛","🍜","🍝","🍠","🍢","🍣","🍤",
                    "🍥","🥮","🍡","🥟","🥠","🥡","🦀","🦞","🦐","🦑","🦪","🍦","🍧","🍨","🍩","🍪","🎂","🍰","🧁","🥧","🍫","🍬","🍭","🍮","🍯"
                }),
                ["⚽ Activities"] = ("⚽ Activities", new[] {
                    "⚽","🏀","🏈","⚾","🥎","🎾","🏐","🏉","🥏","🎱","🪀","🏓","🏸","🏒","🏑","🥍","🏏","🪃","🥅","⛳",
                    "🪁","🏹","🎣","🤿","🥊","🥋","🎽","🛹","🛼","🛷","⛸️","🥌","🎿","⛷️","🏂","🪂","🏋️","🤸","🤼","🤽",
                    "🤾","🤺","⛹️","🏊","🚣","🧗","🚵","🚴","🏆","🥇","🥈","🥉","🏅","🎖️","🏵️","🎗️","🎫","🎟️","🎪","🎭",
                    "🎨","🎬","🎤","🎧","🎼","🎹","🥁","🪘","🎷","🎺","🪗","🎸","🪕","🎻","🪈","🎲","♟️","🎯","🎳","🎮","🕹️","🎰"
                }),
                ["🚗 Travel"] = ("🚗 Travel", new[] {
                    "🚗","🚕","🚙","🚌","🚎","🏎️","🚓","🚑","🚒","🚐","🛻","🚚","🚛","🚜","🏍️","🛵","🚲","🛴","🛹","🛼",
                    "🚁","🛸","🚀","🛩️","✈️","🛫","🛬","🪂","💺","🚢","⛵","🚤","🛥️","🛳️","⛴️","🚂","🚃","🚄","🚅","🚆",
                    "🚇","🚈","🚉","🚊","🚝","🚞","🚋","🚃","🏠","🏡","🏢","🏣","🏤","🏥","🏦","🏨","🏩","🏪","🏫","🏬",
                    "🏭","🏯","🏰","💒","🗼","🗽","⛪","🕌","🛕","🕍","⛩️","🕋","⛲","⛺","🌁","🌃","🏙️","🌄","🌅","🌆","🌇","🌉","🗻","🌋","🏔️","⛰️"
                }),
                ["💡 Objects"] = ("💡 Objects", new[] {
                    "⌚","📱","📲","💻","⌨️","🖥️","🖨️","🖱️","🖲️","🕹️","🗜️","💽","💾","💿","📀","📼","📷","📸","📹","🎥",
                    "📽️","🎞️","📞","☎️","📟","📠","📺","📻","🎙️","🎚️","🎛️","🧭","⏱️","⏲️","⏰","🕰️","⌛","⏳","📡","🔋",
                    "🪫","🔌","💡","🔦","🕯️","🪔","🧯","🛢️","💸","💵","💴","💶","💷","🪙","💰","💳","💎","⚖️","🪜","🧰",
                    "🪛","🔧","🔨","⚒️","🛠️","⛏️","🪚","🔩","⚙️","🪤","🧱","⛓️","🧲","🔫","💣","🧨","🪓","🔪","🗡️","⚔️",
                    "🛡️","🚬","⚰️","🪦","⚱️","🏺","🔮","📿","🧿","🪬","💈","⚗️","🔭","🔬","🕳️","🩹","🩺","🩻","🩼","💊","💉","🩸","🧬","🦠","🧫","🧪"
                }),
                ["🔣 Symbols"] = ("🔣 Symbols", new[] {
                    "❤️","🔴","🟠","🟡","🟢","🔵","🟣","⚫","⚪","🟤","🔶","🔷","🔸","🔹","🔺","🔻","💠","🔘","🔳","🔲",
                    "✅","☑️","✔️","❌","❎","➕","➖","➗","✖️","♾️","‼️","⁉️","❓","❔","❕","❗","〰️","💱","💲","⚕️",
                    "♻️","⚜️","🔱","📛","🔰","⭕","✳️","❇️","🔆","🔅","〽️","⚠️","🚸","🔅","♈","♉","♊","♋","♌","♍",
                    "♎","♏","♐","♑","♒","♓","⛎","🔀","🔁","🔂","▶️","⏩","⏭️","⏯️","◀️","⏪","⏮️","🔼","⏫","🔽","⏬",
                    "⏸️","⏹️","⏺️","⏏️","🎦","🔅","📶","📳","📴","🏁","🚩","🎌","🏴","🏳️","🏳️‍🌈","🏳️‍⚧️","🏴‍☠️"
                }),
                ["🚩 Flags"] = ("🚩 Flags", new[] {
                    "🇺🇸","🇬🇧","🇫🇷","🇩🇪","🇮🇹","🇪🇸","🇵🇹","🇧🇷","🇨🇦","🇦🇺","🇯🇵","🇰🇷","🇨🇳","🇮🇳","🇷🇺","🇲🇽","🇦🇷","🇨🇴","🇹🇷","🇸🇦",
                    "🇦🇪","🇮🇩","🇹🇭","🇻🇳","🇵🇭","🇲🇾","🇸🇬","🇳🇬","🇿🇦","🇪🇬","🇰🇪","🇬🇭","🇪🇹","🇵🇰","🇧🇩","🇱🇰","🇳🇵","🇮🇱","🇵🇸","🇮🇶",
                    "🇮🇷","🇦🇫","🇺🇦","🇵🇱","🇳🇱","🇧🇪","🇨🇭","🇦🇹","🇸🇪","🇳🇴","🇩🇰","🇫🇮","🇮🇪","🇬🇷","🇨🇿","🇷🇴","🇭🇺","🇧🇬","🇭🇷","🇷🇸"
                })
            };

            foreach (var kvp in cats)
            {
                foreach (var e in kvp.Value.emojis)
                    _allEmojis.Add(new EmojiItem { Emoji = e, Name = e, Category = kvp.Key });
            }
        }

        private void BuildCategoryTabs()
        {
            CategoryTabs.Children.Clear();
            var categories = _allEmojis.Select(e => e.Category).Distinct().ToList();
            foreach (var cat in categories)
            {
                var emojiText = new Emoji.Wpf.TextBlock { Text = cat.Split(' ')[0], FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var btn = new System.Windows.Controls.Button
                {
                    Content = emojiText,
                    Width = 34, Height = 34,
                    Margin = new Thickness(0, 0, 3, 0),
                    Padding = new Thickness(0),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = cat,
                    Tag = cat
                };
                btn.Click += CategoryTab_Click;
                if (cat == _currentCategory)
                    btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x20, 0x63, 0x66, 0xF1));
                CategoryTabs.Children.Add(btn);
            }
        }

        private void CategoryTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string cat)
            {
                _currentCategory = cat;
                EmojiSearchBox.Text = "";
                BuildCategoryTabs();
                FilterEmojis();
            }
        }

        private void FilterEmojis()
        {
            string search = EmojiSearchBox?.Text?.Trim().ToLowerInvariant() ?? "";
            EmojiSearchPlaceholder.Visibility = string.IsNullOrEmpty(search) ? Visibility.Visible : Visibility.Collapsed;

            IEnumerable<EmojiItem> filtered;
            if (!string.IsNullOrEmpty(search))
                filtered = _allEmojis.Where(e => e.Emoji.Contains(search) || e.Name.ToLowerInvariant().Contains(search) || e.Category.ToLowerInvariant().Contains(search));
            else
                filtered = _allEmojis.Where(e => e.Category == _currentCategory);

            EmojiGrid.ItemsSource = filtered.ToList();
        }

        private void Emoji_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string emoji && !string.IsNullOrEmpty(emoji))
            {
                try
                {
                    Clipboard.SetText(emoji);
                    ToastWindow.ShowToast($"Copied {emoji}");
                }
                catch { }
            }
        }

        private void EmojiSearchBox_TextChanged(object sender, TextChangedEventArgs e) => FilterEmojis();
        private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Window_Deactivated(object sender, EventArgs e) => Close();
    }
}
