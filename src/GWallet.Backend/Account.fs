﻿namespace GWallet.Backend

open System
open System.Linq
open System.IO

module Account =

    let private GetBalanceInternal(account: IAccount) (onlyConfirmed: bool) (mode: Mode): Async<decimal> =
        async {
            if account.Currency.IsEtherBased() then
                if (onlyConfirmed) then
                    return! Ether.Account.GetConfirmedBalance account mode
                else
                    return! Ether.Account.GetUnconfirmedPlusConfirmedBalance account mode
            elif (account.Currency.IsUtxo()) then
                if (onlyConfirmed) then
                    return! UtxoCoin.Account.GetConfirmedBalance account mode
                else
                    return! UtxoCoin.Account.GetUnconfirmedPlusConfirmedBalance account mode
            else
                return failwith (sprintf "Unknown currency %A" account.Currency)
        }

    let private GetBalanceFromServer (account: IAccount) (onlyConfirmed: bool) (mode: Mode)
                                         : Async<Option<decimal>> =
        async {
            try
                let! balance = GetBalanceInternal account onlyConfirmed mode
                return Some balance
            with
            | ex ->
                if (FSharpUtil.FindException<ServerUnavailabilityException> ex).IsSome then
                    return None
                else
                    return raise (FSharpUtil.ReRaise ex)
        }

    let GetUnconfirmedPlusConfirmedBalance(account: IAccount) =
        GetBalanceFromServer account false

    let GetConfirmedBalance(account: IAccount) =
        GetBalanceFromServer account true

    let private GetShowableBalanceInternal(account: IAccount) (mode: Mode): Async<Option<decimal>> = async {
        let! confirmed = GetConfirmedBalance account mode
        if mode = Mode.Fast && Caching.Instance.FirstRun then
            return confirmed
        else
            let! unconfirmed = GetUnconfirmedPlusConfirmedBalance account mode
            match unconfirmed,confirmed with
            | Some unconfirmedAmount,Some confirmedAmount ->
                if (unconfirmedAmount < confirmedAmount) then
                    return unconfirmed
                else
                    return confirmed
            | _ -> return confirmed
    }

    let GetShowableBalance(account: IAccount) (mode: Mode): Async<MaybeCached<decimal>> =
        async {
            let! maybeBalance = GetShowableBalanceInternal account mode
            match maybeBalance with
            | None ->
                return NotFresh(Caching.Instance.RetreiveLastCompoundBalance account.PublicAddress account.Currency)
            | Some balance ->
                let compoundBalance,_ =
                    Caching.Instance.RetreiveAndUpdateLastCompoundBalance account.PublicAddress
                                                                          account.Currency
                                                                          balance
                return Fresh compoundBalance
        }

    let GetAllActiveAccounts(): seq<IAccount> =
        seq {
            let allCurrencies = Currency.GetAll()

            for currency in allCurrencies do

                for accountFile in Config.GetAllReadOnlyAccounts(currency) do
                    let fileName = Path.GetFileName(accountFile.FullName)
                    let publicKeyContents = File.ReadAllText(accountFile.FullName).Trim()
                    if currency.IsUtxo() then
                        let publicKey =
                            match publicKeyContents.Length with
                            | 0 -> None
                            | _ -> Some publicKeyContents
                        yield UtxoCoin.ReadOnlyUtxoAccount(currency, fileName) :> IAccount
                    else
                        yield ReadOnlyAccount(currency, fileName) :> IAccount

                let fromAccountFileToPublicAddress =
                    if currency.IsUtxo() then
                        UtxoCoin.Account.GetPublicAddressFromAccountFile currency
                    elif currency.IsEtherBased() then
                        Ether.Account.GetPublicAddressFromAccountFile
                    else
                        failwith (sprintf "Unknown currency %A" currency)
                for accountFile in Config.GetAllNormalAccounts [currency] do
                    yield NormalAccount(currency, accountFile, fromAccountFileToPublicAddress) :> IAccount
        }

    let GetNormalAccountsPairingInfoForWatchWallet(): Option<WatchWalletInfo> =
        let allCurrencies = Currency.GetAll()

        let utxoCurrencyAccountFiles =
            Config.GetAllNormalAccounts (allCurrencies.Where(fun currency -> currency.IsUtxo()))
        let etherCurrencyAccountFiles =
            Config.GetAllNormalAccounts (allCurrencies.Where(fun currency -> currency.IsEtherBased()))
        if (not (utxoCurrencyAccountFiles.Any())) || (not (etherCurrencyAccountFiles.Any())) then
            None
        else
            let firstUtxoAccountFile = utxoCurrencyAccountFiles.First()
            let utxoCoinPublicKey = UtxoCoin.Account.GetPublicKeyFromAccountFile firstUtxoAccountFile
            let firstEtherAccountFile = etherCurrencyAccountFiles.First()
            let etherPublicAddress = Ether.Account.GetPublicAddressFromAccountFile firstEtherAccountFile
            Some {
                UtxoCoinPublicKey = utxoCoinPublicKey.ToString()
                EtherPublicAddress = etherPublicAddress
            }


    let GetArchivedAccountsWithPositiveBalance(): Async<seq<ArchivedAccount*decimal>> =
        let asyncJobs = seq<Async<ArchivedAccount*Option<decimal>>> {
            let allCurrencies = Currency.GetAll()

            for currency in allCurrencies do
                let fromAccountFileToPublicAddress =
                    if currency.IsUtxo() then
                        UtxoCoin.Account.GetPublicAddressFromUnencryptedPrivateKey currency
                    elif currency.IsEtherBased() then
                        Ether.Account.GetPublicAddressFromUnencryptedPrivateKey
                    else
                        failwith (sprintf "Unknown currency %A" currency)

                for accountFile in Config.GetAllArchivedAccounts(currency) do
                    let account = ArchivedAccount(currency, accountFile, fromAccountFileToPublicAddress)
                    let maybeBalance = GetUnconfirmedPlusConfirmedBalance account Mode.Fast
                    yield async {
                        let! unconfirmedBalance = maybeBalance
                        let positiveBalance =
                            match unconfirmedBalance with
                            | Some balance ->
                                if (balance > 0m) then
                                    Some(balance)
                                else
                                    None
                            | _ ->
                                None
                        return account,positiveBalance
                    }
        }
        let executedBalances = Async.Parallel asyncJobs
        async {
            let! accountAndPositiveBalances = executedBalances
            return seq {
                for account,maybePositiveBalance in accountAndPositiveBalances do
                    match maybePositiveBalance with
                    | Some positiveBalance -> yield account,positiveBalance
                    | _ -> ()
            }
        }

    // TODO: add tests for these (just in case address validation breaks after upgrading our dependencies)
    let ValidateAddress (currency: Currency) (address: string) =
        if currency.IsEtherBased() then
            Ether.Account.ValidateAddress currency address
        elif currency.IsUtxo() then
            UtxoCoin.Account.ValidateAddress currency address
        else
            failwith (sprintf "Unknown currency %A" currency)

    let EstimateFee (account: IAccount) (amount: TransferAmount) destination: Async<IBlockchainFeeInfo> =
        async {
            match account with
            | :? UtxoCoin.IUtxoAccount as utxoAccount ->
                if not (account.Currency.IsUtxo()) then
                    failwithf "Currency %A not Utxo-type but account is? report this bug" account.Currency
                let! fee = UtxoCoin.Account.EstimateFee utxoAccount amount destination
                return fee :> IBlockchainFeeInfo
            | _ ->
                if not (account.Currency.IsEtherBased()) then
                    failwithf "Currency %A not ether based and not UTXO either? not supported, report this bug"
                        account.Currency
                let! fee = Ether.Account.EstimateFee account amount destination
                return fee :> IBlockchainFeeInfo
        }

    // FIXME: broadcasting shouldn't just get N consistent replies from FaultToretantClient,
    // but send it to as many as possible, otherwise it could happen that some server doesn't
    // broadcast it even if you sent it
    let BroadcastTransaction (trans: SignedTransaction<_>): Async<Uri> =
        async {
            let currency = trans.TransactionInfo.Proposal.Amount.Currency

            let! txId =
                if currency.IsEtherBased() then
                    Ether.Account.BroadcastTransaction trans
                elif currency.IsUtxo() then
                    UtxoCoin.Account.BroadcastTransaction currency trans
                else
                    failwith (sprintf "Unknown currency %A" currency)

            let feeCurrency = trans.TransactionInfo.Metadata.Currency
            Caching.Instance.StoreOutgoingTransaction
                trans.TransactionInfo.Proposal.OriginAddress
                currency
                feeCurrency
                txId
                trans.TransactionInfo.Proposal.Amount.ValueToSend
                trans.TransactionInfo.Metadata.FeeValue

            return BlockExplorer.GetTransaction currency txId
        }

    let SignTransaction (account: NormalAccount)
                        (destination: string)
                        (amount: TransferAmount)
                        (transactionMetadata: IBlockchainFeeInfo)
                        (password: string) =

        match transactionMetadata with
        | :? Ether.TransactionMetadata as etherTxMetada ->
            Ether.Account.SignTransaction
                  account
                  etherTxMetada
                  destination
                  amount
                  password
        | :? UtxoCoin.TransactionMetadata as btcTxMetadata ->
            match account with
            | :? UtxoCoin.NormalUtxoAccount as utxoAccount ->
                UtxoCoin.Account.SignTransaction
                    utxoAccount
                    btcTxMetadata
                    destination
                    amount
                    password
            | _ ->
                failwith "An UtxoCoin.TransactionMetadata should come with a UtxoCoin.Account"

        | _ -> failwith "fee type unknown"

    let private CreateArchivedAccount (currency: Currency) (unencryptedPrivateKey: string): ArchivedAccount =
        let fromUnencryptedPrivateKeyToPublicAddressFunc =
            if currency.IsUtxo() then
                UtxoCoin.Account.GetPublicAddressFromUnencryptedPrivateKey currency
            elif currency.IsEther() then
                Ether.Account.GetPublicAddressFromUnencryptedPrivateKey
            else
                failwith (sprintf "Unknown currency %A" currency)
        let fileName = fromUnencryptedPrivateKeyToPublicAddressFunc unencryptedPrivateKey
        let newAccountFile = Config.AddArchivedAccount currency fileName unencryptedPrivateKey
        ArchivedAccount(currency, newAccountFile, fromUnencryptedPrivateKeyToPublicAddressFunc)

    let Archive (account: NormalAccount)
                (password: string)
                : unit =
        let currency = (account:>IAccount).Currency
        let privateKeyAsString =
            if currency.IsUtxo() then
                let privKey = UtxoCoin.Account.GetPrivateKey account password
                privKey.GetWif(UtxoCoin.Account.GetNetwork currency).ToWif()
            elif currency.IsEther() then
                let privKey = Ether.Account.GetPrivateKey account password
                privKey.GetPrivateKey()
            else
                failwith (sprintf "Unknown currency %A" currency)
        CreateArchivedAccount currency privateKeyAsString |> ignore
        Config.RemoveNormal account

    let SweepArchivedFunds (account: ArchivedAccount)
                           (balance: decimal)
                           (destination: IAccount)
                           (txMetadata: IBlockchainFeeInfo) =
        match txMetadata with
        | :? Ether.TransactionMetadata as etherTxMetadata ->
            Ether.Account.SweepArchivedFunds account balance destination etherTxMetadata
        | :? UtxoCoin.TransactionMetadata as utxoTxMetadata ->
            match account with
            | :? UtxoCoin.ArchivedUtxoAccount as utxoAccount ->
                UtxoCoin.Account.SweepArchivedFunds utxoAccount balance destination utxoTxMetadata
            | _ ->
                failwithf "If tx metadata is UTXO type, archived account should be too"
        | _ -> failwith "tx metadata type unknown"

    let SendPayment (account: NormalAccount)
                    (txMetadata: IBlockchainFeeInfo)
                    (destination: string)
                    (amount: TransferAmount)
                    (password: string)
                    =
        let baseAccount = account :> IAccount
        if (baseAccount.PublicAddress.Equals(destination, StringComparison.InvariantCultureIgnoreCase)) then
            raise DestinationEqualToOrigin

        let currency = baseAccount.Currency
        ValidateAddress currency destination

        async {
            let! txId =
                match txMetadata with
                | :? UtxoCoin.TransactionMetadata as btcTxMetadata ->
                    if not (currency.IsUtxo()) then
                        failwithf "Currency %A not Utxo-type but tx metadata is? report this bug" currency
                    match account with
                    | :? UtxoCoin.NormalUtxoAccount as utxoAccount ->
                        UtxoCoin.Account.SendPayment utxoAccount btcTxMetadata destination amount password
                    | _ ->
                        failwith "Account not Utxo-type but tx metadata is? report this bug"

                | :? Ether.TransactionMetadata as etherTxMetadata ->
                    if not (currency.IsEtherBased()) then
                        failwith "Account not Utxo-type but tx metadata is? report this bug"
                    Ether.Account.SendPayment account etherTxMetadata destination amount password
                | _ ->
                    failwithf "Unknown tx metadata type"

            let feeCurrency = txMetadata.Currency
            Caching.Instance.StoreOutgoingTransaction
                baseAccount.PublicAddress
                currency
                feeCurrency
                txId
                amount.ValueToSend
                txMetadata.FeeValue

            return BlockExplorer.GetTransaction currency txId
        }

    let SignUnsignedTransaction (account)
                                (unsignedTrans: UnsignedTransaction<IBlockchainFeeInfo>)
                                password =
        let rawTransaction = SignTransaction account
                                 unsignedTrans.Proposal.DestinationAddress
                                 unsignedTrans.Proposal.Amount
                                 unsignedTrans.Metadata
                                 password

        { TransactionInfo = unsignedTrans; RawTransaction = rawTransaction }

    let public ExportSignedTransaction (trans: SignedTransaction<_>) =
        Marshalling.Serialize trans

    let SaveSignedTransaction (trans: SignedTransaction<_>) (filePath: string) =

        let json =
            match trans.TransactionInfo.Metadata.GetType() with
            | t when t = typeof<Ether.TransactionMetadata> ->
                let unsignedEthTx = {
                    Metadata = box trans.TransactionInfo.Metadata :?> Ether.TransactionMetadata;
                    Proposal = trans.TransactionInfo.Proposal;
                    Cache = trans.TransactionInfo.Cache;
                }
                let signedEthTx = {
                    TransactionInfo = unsignedEthTx;
                    RawTransaction = trans.RawTransaction;
                }
                ExportSignedTransaction signedEthTx
            | t when t = typeof<UtxoCoin.TransactionMetadata> ->
                let unsignedBtcTx = {
                    Metadata = box trans.TransactionInfo.Metadata :?> UtxoCoin.TransactionMetadata;
                    Proposal = trans.TransactionInfo.Proposal;
                    Cache = trans.TransactionInfo.Cache;
                }
                let signedBtcTx = {
                    TransactionInfo = unsignedBtcTx;
                    RawTransaction = trans.RawTransaction;
                }
                ExportSignedTransaction signedBtcTx
            | _ -> failwith "Unknown miner fee type"

        File.WriteAllText(filePath, json)

    let AddPublicWatcher (watchWalletInfo: WatchWalletInfo) =
        for etherCurrency in Currency.GetAll().Where(fun currency -> currency.IsEtherBased()) do
            ReadOnlyAccount(etherCurrency, watchWalletInfo.EtherPublicAddress) |> Config.AddReadonly

        for utxoCurrency in Currency.GetAll().Where(fun currency -> currency.IsUtxo()) do
            let address =
                UtxoCoin.Account.GetPublicAddressFromPublicKey utxoCurrency
                                                               (NBitcoin.PubKey(watchWalletInfo.UtxoCoinPublicKey))
            UtxoCoin.ReadOnlyUtxoAccount(utxoCurrency, address, Some watchWalletInfo.UtxoCoinPublicKey) |> Config.AddReadonly

    let RemovePublicWatcher (account: ReadOnlyAccount) =
        Config.RemoveReadonly account

    let private CreateConceptEtherAccountInternal (password: string) (seed: array<byte>)
                                                 : Async<(string*string)*(FileInfo->string)> =
        async {
            let! fileName,encryptedPrivateKeyInJson = Ether.Account.Create password seed
            return (fileName,encryptedPrivateKeyInJson), Ether.Account.GetPublicAddressFromAccountFile
        }

    let private CreateConceptAccountInternal (currency: Currency) (password: string) (seed: array<byte>)
                                            : Async<(string*string)*(FileInfo->string)> =
        async {
            if currency.IsUtxo() then
                let! publicKey,encryptedPrivateKey = UtxoCoin.Account.Create currency password seed
                return (publicKey,encryptedPrivateKey), UtxoCoin.Account.GetPublicAddressFromAccountFile currency
            elif currency.IsEtherBased() then
                return! CreateConceptEtherAccountInternal password seed
            else
                return failwith (sprintf "Unknown currency %A" currency)
        }


    let CreateConceptAccount (currency: Currency) (password: string) (seed: array<byte>)
                            : Async<ConceptAccount> =
        async {
            let! (fileName, encryptedPrivateKey), fromEncPrivKeyToPublicAddressFunc =
                CreateConceptAccountInternal currency password seed
            return {
                       Currency = currency;
                       FileNameAndContent = fileName,encryptedPrivateKey;
                       ExtractPublicAddressFromConfigFileFunc = fromEncPrivKeyToPublicAddressFunc;
                   }
        }

    let private CreateConceptAccountAux (currency: Currency) (password: string) (seed: array<byte>)
                            : Async<List<ConceptAccount>> =
        async {
            let! singleAccount = CreateConceptAccount currency password seed
            return singleAccount::List.Empty
        }

    let CreateEtherNormalAccounts (password: string) (seed: array<byte>)
                                  : seq<Currency>*Async<List<ConceptAccount>> =
        let etherCurrencies = Currency.GetAll().Where(fun currency -> currency.IsEtherBased())
        let etherAccounts = async {
            let! (fileName, encryptedPrivateKey), fromEncPrivKeyToPublicAddressFunc =
                CreateConceptEtherAccountInternal password seed
            return seq {
                for etherCurrency in etherCurrencies do
                    yield {
                              Currency = etherCurrency;
                              FileNameAndContent = fileName,encryptedPrivateKey;
                              ExtractPublicAddressFromConfigFileFunc = fromEncPrivKeyToPublicAddressFunc
                          }
            } |> List.ofSeq
        }
        etherCurrencies,etherAccounts

    let CreateNormalAccount (conceptAccount: ConceptAccount): NormalAccount =
        let fileName,fileContents = conceptAccount.FileNameAndContent
        let newAccountFile = Config.AddNormalAccount conceptAccount
        NormalAccount(conceptAccount.Currency, newAccountFile, conceptAccount.ExtractPublicAddressFromConfigFileFunc)

    let CreateBaseAccount (passphrase: string)
                          (dobPartOfSalt: DateTime) (emailPartOfSalt: string)
                          (encryptionPassword: string): Async<unit> =

        let salt = sprintf "%s+%s" (dobPartOfSalt.Date.ToString("yyyyMMdd")) (emailPartOfSalt.ToLower())
        let privateKeyBytes = WarpKey.CreatePrivateKey passphrase salt

        let ethCurrencies,etherAccounts = CreateEtherNormalAccounts encryptionPassword privateKeyBytes
        let nonEthCurrencies = Currency.GetAll().Where(fun currency -> not (ethCurrencies.Contains currency))

        let nonEtherAccounts: List<Async<List<ConceptAccount>>> =
            seq {
                // TODO: figure out if we can reuse CPU computation of WIF creation between BTC&LTC
                for nonEthCurrency in nonEthCurrencies do
                    yield CreateConceptAccountAux nonEthCurrency encryptionPassword privateKeyBytes
            } |> List.ofSeq

        let allAccounts = etherAccounts::nonEtherAccounts

        let createAllAccountsJob = Async.Parallel allAccounts

        async {
            let! allCreatedConceptAccounts = createAllAccountsJob
            for accountGroup in allCreatedConceptAccounts do
                for conceptAccount in accountGroup do
                    CreateNormalAccount conceptAccount |> ignore
        }

    let public ExportUnsignedTransactionToJson trans =
        Marshalling.Serialize trans

    let private SerializeUnsignedTransactionPlain (transProposal: UnsignedTransactionProposal)
                                                  (txMetadata: IBlockchainFeeInfo)
                                                      : string =

        ValidateAddress transProposal.Amount.Currency transProposal.DestinationAddress

        let readOnlyAccounts = GetAllActiveAccounts().OfType<ReadOnlyAccount>()

        match txMetadata with
        | :? Ether.TransactionMetadata as etherTxMetadata ->
            Ether.Account.SaveUnsignedTransaction transProposal etherTxMetadata readOnlyAccounts
        | :? UtxoCoin.TransactionMetadata as btcTxMetadata ->
            UtxoCoin.Account.SaveUnsignedTransaction transProposal btcTxMetadata readOnlyAccounts
        | _ -> failwith "fee type unknown"

    let SaveUnsignedTransaction (transProposal: UnsignedTransactionProposal)
                                (txMetadata: IBlockchainFeeInfo)
                                (filePath: string) =
        let json = SerializeUnsignedTransactionPlain transProposal txMetadata
        File.WriteAllText(filePath, json)

    let public ImportUnsignedTransactionFromJson (json: string): UnsignedTransaction<IBlockchainFeeInfo> =

        let transType = Marshalling.ExtractType json

        match transType with
        | _ when transType = typeof<UnsignedTransaction<UtxoCoin.TransactionMetadata>> ->
            let deserializedBtcTransaction: UnsignedTransaction<UtxoCoin.TransactionMetadata> =
                    Marshalling.Deserialize json
            deserializedBtcTransaction.ToAbstract()
        | _ when transType = typeof<UnsignedTransaction<Ether.TransactionMetadata>> ->
            let deserializedBtcTransaction: UnsignedTransaction<Ether.TransactionMetadata> =
                    Marshalling.Deserialize json
            deserializedBtcTransaction.ToAbstract()
        | unexpectedType ->
            raise <| Exception(sprintf "Unknown unsignedTransaction subtype: %s" unexpectedType.FullName)

    let public ImportSignedTransactionFromJson (json: string): SignedTransaction<IBlockchainFeeInfo> =
        let transType = Marshalling.ExtractType json

        match transType with
        | _ when transType = typeof<SignedTransaction<UtxoCoin.TransactionMetadata>> ->
            let deserializedBtcTransaction: SignedTransaction<UtxoCoin.TransactionMetadata> =
                    Marshalling.Deserialize json
            deserializedBtcTransaction.ToAbstract()
        | _ when transType = typeof<SignedTransaction<Ether.TransactionMetadata>> ->
            let deserializedBtcTransaction: SignedTransaction<Ether.TransactionMetadata> =
                    Marshalling.Deserialize json
            deserializedBtcTransaction.ToAbstract()
        | unexpectedType ->
            raise <| Exception(sprintf "Unknown signedTransaction subtype: %s" unexpectedType.FullName)

    let LoadSignedTransactionFromFile (filePath: string) =
        let signedTransInJson = File.ReadAllText(filePath)

        ImportSignedTransactionFromJson signedTransInJson

    let LoadUnsignedTransactionFromFile (filePath: string): UnsignedTransaction<IBlockchainFeeInfo> =
        let unsignedTransInJson = File.ReadAllText(filePath)

        ImportUnsignedTransactionFromJson unsignedTransInJson

