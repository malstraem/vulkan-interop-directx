﻿using Microsoft.UI.Xaml;

namespace Interop.WinUI3
{
    public partial class App : Application
    {
        private Window? window;

        public App() => InitializeComponent();

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            window = new MainWindow();
            window.Activate();
        }
    }
}
