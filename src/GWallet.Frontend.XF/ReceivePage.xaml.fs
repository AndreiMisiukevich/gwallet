﻿namespace GWallet.Frontend.XF

open System
open System.Threading.Tasks

open Xamarin.Forms
open Xamarin.Forms.Xaml

open Plugin.Clipboard
open Plugin.Connectivity
open ZXing
open ZXing.Net.Mobile.Forms
open ZXing.Common

open GWallet.Backend

type ReceivePage(account: IAccount,
                 balancesPage: Page) as this =
    inherit ContentPage()
    let _ = base.LoadFromXaml(typeof<ReceivePage>)

    let mainLayout = base.FindByName<StackLayout>("mainLayout")
    do
        this.Init()

    member this.Init() =
        let balanceLabel = mainLayout.FindByName<Label>("balanceLabel")
        let fiatBalanceLabel = mainLayout.FindByName<Label>("fiatBalanceLabel")

        let accountBalance =
            Caching.Instance.RetreiveLastCompoundBalance account.PublicAddress account.Currency
        FrontendHelpers.UpdateBalance (NotFresh accountBalance) account.Currency balanceLabel fiatBalanceLabel
            |> ignore

        balanceLabel.FontSize <- FrontendHelpers.BigFontSize
        fiatBalanceLabel.FontSize <- FrontendHelpers.MediumFontSize

        let size = 200
        let encodingOptions = EncodingOptions(Height = size,
                                              Width = size)
        let barCode = ZXingBarcodeImageView(HorizontalOptions = LayoutOptions.Center,
                                            VerticalOptions = LayoutOptions.Center,
                                            BarcodeFormat = BarcodeFormat.QR_CODE,
                                            BarcodeValue = account.PublicAddress,
                                            HeightRequest = float size,
                                            WidthRequest = float size,
                                            BarcodeOptions = encodingOptions)
        mainLayout.Children.Add(barCode)

        let transactionHistoryButton = Button(Text = "View transaction history...")
        transactionHistoryButton.Clicked.Subscribe(fun _ ->
            Device.OpenUri (BlockExplorer.GetTransactionHistory account)
        ) |> ignore
        mainLayout.Children.Add(transactionHistoryButton)

        if not CrossConnectivity.IsSupported then
            failwith "cross connectivity plugin not supported for this platform?"

        let paymentButton = mainLayout.FindByName<Button> "paymentButton"
        use crossConnectivityInstance = CrossConnectivity.Current
        if crossConnectivityInstance.IsConnected then
            paymentButton.Text <- "Send Payment"
            match accountBalance with
            | Cached(amount,_) ->
                if (amount > 0m) then
                    paymentButton.IsEnabled <- true
            | _ -> ()
        else
            paymentButton.Text <- "Signoff Payment Offline"
            paymentButton.IsEnabled <- true
            transactionHistoryButton.IsEnabled <- false

        // FIXME: report this Xamarin.Forms Mac backend bug (no back button in navigation pages!, so below <workaround>)
        if (Device.RuntimePlatform <> Device.macOS) then () else

        let backButton = Button(Text = "< Go back")
        backButton.Clicked.Subscribe(fun _ ->
            Device.BeginInvokeOnMainThread(fun _ ->
                balancesPage.Navigation.PopAsync() |> FrontendHelpers.DoubleCheckCompletion
            )
        ) |> ignore
        mainLayout.Children.Add(backButton)
        //</workaround>

    member this.OnSendPaymentClicked(sender: Object, args: EventArgs) =
        try
            Device.BeginInvokeOnMainThread(fun _ ->
                try
                    let newReceivePageFunc = (fun _ ->
                        ReceivePage(account, balancesPage) :> Page
                    )
                    let sendPage = SendPage(account, this, newReceivePageFunc)
                    NavigationPage.SetHasNavigationBar(sendPage, false)
                    let navSendPage = NavigationPage sendPage
                    NavigationPage.SetHasNavigationBar(navSendPage, false)
                    

                    this.Navigation.PushAsync navSendPage
                        |> FrontendHelpers.DoubleCheckCompletionNonGeneric
                with
                | :? Exception as ex ->
                    let balanceLabel = mainLayout.FindByName<Label>("balanceLabel")
                    balanceLabel.Text <- ("1:"+ex.ToString())
            )
        with
        | :? Exception as ex ->
            let balanceLabel = mainLayout.FindByName<Label>("balanceLabel")
            Device.BeginInvokeOnMainThread(fun _ ->
                balanceLabel.Text <- ("2:"+ex.ToString())
            )
        ()

    member this.OnCopyToClipboardClicked(sender: Object, args: EventArgs) =
        let copyToClipboardButton = base.FindByName<Button>("copyToClipboardButton")
        FrontendHelpers.ChangeTextAndChangeBack copyToClipboardButton "Copied"

        CrossClipboard.Current.SetText account.PublicAddress
        ()
