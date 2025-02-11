﻿using CharacterMap.Core;
using CharacterMap.Helpers;
using CharacterMap.Models;
using CharacterMap.Services;
using CharacterMap.ViewModels;
using Microsoft.Toolkit.Mvvm.DependencyInjection;
using Microsoft.Toolkit.Mvvm.Messaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace CharacterMap.Controls
{
    public class CreateCollectionDialogTemplateSettings : ViewModelBase
    {
        private string _collectionTitle;
        public string CollectionTitle
        {
            get => _collectionTitle;
            set { if (Set(ref _collectionTitle, value)) OnCollectionTitleChanged(); }
        }

        private bool _isCollectionTitleValid;
        public bool IsCollectionTitleValid
        {
            get => _isCollectionTitleValid;
            private set => Set(ref _isCollectionTitleValid, value);
        }

        private void OnCollectionTitleChanged()
        {
            IsCollectionTitleValid = !string.IsNullOrWhiteSpace(CollectionTitle);
        }
    }

    public sealed partial class CreateCollectionDialog : ContentDialog
    {
        public CreateCollectionDialogTemplateSettings TemplateSettings { get; }

        public bool IsRenameMode { get; }

        public object Result { get; private set; }


        private UserFontCollection _collection = null;

        public CreateCollectionDialog(UserFontCollection collection = null)
        {
            _collection = collection;
            TemplateSettings = new CreateCollectionDialogTemplateSettings();
            this.InitializeComponent();

            if (_collection != null)
            {
                IsRenameMode = true;
                this.Title = Localization.Get("DigRenameCollection/Title");
                this.PrimaryButtonText = Localization.Get("DigRenameCollection/PrimaryButtonText");
                TemplateSettings.CollectionTitle = _collection.Name;
            }
        }


        private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var d = args.GetDeferral();

            var collections = Ioc.Default.GetService<UserCollectionsService>();

            if (IsRenameMode)
            {
                this.IsPrimaryButtonEnabled = false;
                this.IsSecondaryButtonEnabled = false;
                InputBox.IsEnabled = false;

                await collections.RenameCollectionAsync(TemplateSettings.CollectionTitle, _collection);
                d.Complete();

                await Task.Yield();
                WeakReferenceMessenger.Default.Send(new CollectionsUpdatedMessage());
            }
            else
            {
                var collection = await collections.CreateCollectionAsync(TemplateSettings.CollectionTitle);
                var result = await collections.AddToCollectionAsync(this.DataContext as InstalledFont, collection);
                Result = result;
                d.Complete();

                await Task.Yield();
                if (result.Success)
                    WeakReferenceMessenger.Default.Send(new AppNotificationMessage(true, result));

            }
        }
    }
}
