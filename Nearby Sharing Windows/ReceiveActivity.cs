﻿using Android.Bluetooth;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.ProgressIndicator;
using ShortDev.Android.UI;
using ShortDev.Microsoft.ConnectedDevices;
using ShortDev.Microsoft.ConnectedDevices.Encryption;
using ShortDev.Microsoft.ConnectedDevices.NearShare;
using ShortDev.Microsoft.ConnectedDevices.Platforms;
using ShortDev.Microsoft.ConnectedDevices.Transports;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;
using SystemDebug = System.Diagnostics.Debug;

namespace Nearby_Sharing_Windows;

[Activity(Label = "@string/app_name", Theme = "@style/AppTheme", ConfigurationChanges = UIHelper.ConfigChangesFlags)]
public sealed class ReceiveActivity : AppCompatActivity, INearSharePlatformHandler
{
    BluetoothAdapter? _btAdapter;

    [AllowNull] TextView debugLogTextView;

    [AllowNull] AdapterDescriptor<TransferToken> adapterDescriptor;
    [AllowNull] RecyclerView notificationsRecyclerView;
    readonly List<TransferToken> _notifications = new();

    PhysicalAddress? btAddress = null;
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        if (ReceiveSetupActivity.IsSetupRequired(this) || !ReceiveSetupActivity.TryGetBtAddress(this, out btAddress) || btAddress == null)
        {
            StartActivity(new Android.Content.Intent(this, typeof(ReceiveSetupActivity)));

            Finish();
            return;
        }

        SetContentView(Resource.Layout.activity_receive);

        UIHelper.RequestReceivePermissions(this);
        UIHelper.SetupToolBar(this, GetString(Resource.String.app_titlebar_title_receive));

        notificationsRecyclerView = FindViewById<RecyclerView>(Resource.Id.notificationsRecyclerView)!;
        notificationsRecyclerView.SetLayoutManager(new LinearLayoutManager(this));

        adapterDescriptor = new(
            Resource.Layout.item_transfer_notification,
            (view, transfer) =>
            {
                var acceptButton = view.FindViewById<Button>(Resource.Id.acceptButton)!;
                var openButton = view.FindViewById<Button>(Resource.Id.openButton)!;
                var fileNameTextView = view.FindViewById<TextView>(Resource.Id.fileNameTextView)!;
                var detailsTextView = view.FindViewById<TextView>(Resource.Id.detailsTextView)!;

                if (transfer is FileTransferToken fileTransfer)
                {
                    fileNameTextView.Text = fileTransfer.FileName;
                    detailsTextView.Text = $"{fileTransfer.DeviceName} • {FileTransferToken.FormatFileSize(fileTransfer.FileSize)}";

                    var loadingProgressIndicator = view.FindViewById<CircularProgressIndicator>(Resource.Id.loadingProgressIndicator)!;
                    Action onCompleted = () =>
                    {
                        acceptButton.Visibility = ViewStates.Gone;
                        loadingProgressIndicator.Visibility = ViewStates.Gone;
                        openButton.Visibility = ViewStates.Visible;
                        openButton.SetOnClickListener(new DelegateClickListener((s, e) => UIHelper.OpenFile(this, GetFilePath(fileTransfer.FileName))));
                    };
                    if (fileTransfer.IsTransferComplete)
                        onCompleted();
                    else
                    {
                        Action onAccept = () =>
                        {
                            if (!fileTransfer.IsAccepted)
                                fileTransfer.Accept(CreateFile(fileTransfer.FileName));

                            acceptButton.Visibility = ViewStates.Gone;
                            loadingProgressIndicator.Visibility = ViewStates.Visible;

                            loadingProgressIndicator.Progress = 0;
                            Action<bool> onProgress = (animate) =>
                            {
                                loadingProgressIndicator.Indeterminate = false;

                                int progress = Math.Min((int)(fileTransfer.ReceivedBytes * 100 / fileTransfer.FileSize), 100);
                                if (OperatingSystem.IsAndroidVersionAtLeast(24))
                                    loadingProgressIndicator.SetProgress(progress, animate);
                                else
                                    loadingProgressIndicator.Progress = progress;

                                if (fileTransfer.IsTransferComplete)
                                    onCompleted();
                            };
                            fileTransfer.SetProgressListener((s) => RunOnUiThread(() => onProgress(/*animate*/true)));
                            loadingProgressIndicator.Indeterminate = true;
                        };
                        if (fileTransfer.IsAccepted)
                            onAccept();
                        else
                            acceptButton.SetOnClickListener(new DelegateClickListener((s, e) => onAccept()));
                    }
                }
                else if (transfer is UriTransferToken uriTranfer)
                {
                    fileNameTextView.Text = uriTranfer.Uri;
                    detailsTextView.Text = uriTranfer.DeviceName;

                    acceptButton.Visibility = ViewStates.Gone;
                    openButton.Visibility = ViewStates.Visible;

                    openButton.SetOnClickListener(new DelegateClickListener((s, e) => UIHelper.DisplayWebSite(this, uriTranfer.Uri)));
                }

                view.FindViewById<Button>(Resource.Id.cancelButton)!.SetOnClickListener(new DelegateClickListener((s, e) =>
                {
                    _notifications.Remove(transfer);
                    UpdateUI();

                    if (transfer is FileTransferToken fileTransfer)
                        fileTransfer.Cancel();
                }));
            }
        );
    }

    CancellationTokenSource? _cancellationTokenSource;
    ConnectedDevicesPlatform? _cdp;
    void InitializeCDP()
    {
        if (btAddress == null)
            throw new NullReferenceException(nameof(btAddress));

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = new();

        var service = (BluetoothManager)GetSystemService(BluetoothService)!;
        _btAdapter = service.Adapter!;

        FindViewById<TextView>(Resource.Id.deviceInfoTextView)!.Text = this.Localize(
            Resource.String.visible_as_template,
            $"{_btAdapter.Name!}.\n" +
            $"Address: {btAddress.ToStringFormatted()}\n" +
            $"IP-Address: {AndroidNetworkHandler.GetLocalIp(this)}");
        debugLogTextView = FindViewById<TextView>(Resource.Id.debugLogTextView)!;

        SystemDebug.Assert(_cdp == null);

        _cdp = new(new()
        {
            Type = DeviceType.Android,
            Name = _btAdapter.Name ?? throw new NullReferenceException("Could not find device name"),
            OemModelName = Build.Model ?? string.Empty,
            OemManufacturerName = Build.Manufacturer ?? string.Empty,
            DeviceCertificate = ConnectedDevicesPlatform.CreateDeviceCertificate(CdpEncryptionParams.Default),
            LoggerFactory = ConnectedDevicesPlatform.CreateLoggerFactory(Log)
        });

        AndroidBluetoothHandler bluetoothHandler = new(this, _btAdapter, btAddress);
        _cdp.AddTransport<BluetoothTransport>(new(bluetoothHandler));

        AndroidNetworkHandler networkHandler = new(this, this);
        _cdp.AddTransport<NetworkTransport>(new(networkHandler));

        _cdp.Listen(_cancellationTokenSource.Token);
        _cdp.Advertise(new CdpAdvertisement(
            DeviceType.Android,
            btAddress, // "00:fa:21:3e:fb:19"
            _btAdapter.Name!
        ), _cancellationTokenSource.Token);

        NearShareReceiver.Start(_cdp, this);
    }

    string GetFilePath(string name)
    {
        var downloadDir = Path.Combine(GetExternalMediaDirs()?.FirstOrDefault()?.AbsolutePath ?? "/sdcard/", "Download");
        if (!Directory.Exists(downloadDir))
            Directory.CreateDirectory(downloadDir);

        return Path.Combine(
            downloadDir,
            name
        );
    }

    FileStream CreateFile(string name)
    {
        var path = GetFilePath(name);
        Log($"Saving file to \"{path}\"");
        return File.Create(path);

        // ToDo: OutputStream cannot seek!
        //ContentValues contentValues = new();
        //contentValues.Put(MediaStore.IMediaColumns.RelativePath, Path.Combine(Android.OS.Environment.DirectoryDownloads!, name));
        //var uri = ContentResolver!.Insert(MediaStore.Files.GetContentUri("external")!, contentValues)!;
        //return ContentResolver!.OpenOutputStream(uri, "rwt")!;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        if (!grantResults.All((x) => x != Permission.Granted))
            InitializeCDP();
        else
            Toast.MakeText(this, this.Localize(Resource.String.receive_missing_permissions), ToastLength.Long)!.Show();
    }

    public override bool OnCreateOptionsMenu(IMenu? menu)
        => UIHelper.OnCreateOptionsMenu(this, menu);

    public override bool OnOptionsItemSelected(IMenuItem item)
        => UIHelper.OnOptionsItemSelected(this, item);

    public override void Finish()
    {
        _cancellationTokenSource?.Cancel();
        _cdp?.Dispose();
        NearShareReceiver.Stop();
        base.Finish();
    }



    public void Log(string message)
    {
        RunOnUiThread(() =>
        {
            debugLogTextView.Text += "\n" + $"[{DateTime.Now:HH:mm:ss}]: {message}";
        });
    }

    void UpdateUI()
    {
        RunOnUiThread(() =>
        {
            notificationsRecyclerView.SetAdapter(adapterDescriptor.CreateRecyclerViewAdapter(_notifications));
        });
    }

    public void OnReceivedUri(UriTransferToken transfer)
    {
        _notifications.Add(transfer);
        UpdateUI();
    }

    public void OnFileTransfer(FileTransferToken transfer)
    {
        _notifications.Add(transfer);
        UpdateUI();
    }
}

static class Extensions
{
    public static CdpDevice ToCdp(this BluetoothDevice @this)
        => new(
            @this.Name ?? throw new InvalidDataException("Empty name"),
            new(
                CdpTransportType.Rfcomm,
                @this.Address ?? throw new InvalidDataException("Empty address"),
                Constants.RfcommServiceId
            )
        );

    public static CdpSocket ToCdp(this BluetoothSocket @this)
        => new()
        {
            TransportType = CdpTransportType.Rfcomm,
            InputStream = @this.InputStream ?? throw new NullReferenceException(),
            OutputStream = @this.OutputStream ?? throw new NullReferenceException(),
            RemoteDevice = @this.RemoteDevice!.ToCdp(),
            Close = @this.Close
        };
}