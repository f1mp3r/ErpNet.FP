﻿namespace ErpNet.FP.Core.Drivers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using ErpNet.FP.Core.Configuration;

    /// <summary>
    /// Fiscal printer using the Zfp implementation.
    /// </summary>
    /// <seealso cref="ErpNet.FP.Core.Drivers.BgFiscalPrinter" />
    public partial class BgZfpFiscalPrinter : BgFiscalPrinter
    {
        public BgZfpFiscalPrinter(
            IChannel channel, 
            ServiceOptions serviceOptions, 
            IDictionary<string, string>? options = null)
        : base(channel, serviceOptions, options) { }

        public override string GetTaxGroupText(TaxGroup taxGroup)
        {
            return taxGroup switch
            {
                TaxGroup.TaxGroup1 => "А",
                TaxGroup.TaxGroup2 => "Б",
                TaxGroup.TaxGroup3 => "В",
                TaxGroup.TaxGroup4 => "Г",
                TaxGroup.TaxGroup5 => "Д",
                TaxGroup.TaxGroup6 => "Е",
                TaxGroup.TaxGroup7 => "Ж",
                TaxGroup.TaxGroup8 => "З",
                _ => throw new StandardizedStatusMessageException($"Tax group {taxGroup} unsupported", "E411"),
            };
        }

        public override IDictionary<PaymentType, string> GetPaymentTypeMappings()
        {
            var paymentTypeMappings = new Dictionary<PaymentType, string> {
                { PaymentType.Cash,          "0" },
                { PaymentType.Check,         "1" },
                { PaymentType.Coupons,       "2" },
                { PaymentType.ExtCoupons,    "3" },
                { PaymentType.Packaging,     "4" },
                { PaymentType.InternalUsage, "5" },
                { PaymentType.Damage,        "6" },
                { PaymentType.Card,          "7" },
                { PaymentType.Bank,          "8" },
                { PaymentType.Reserved1,     "9" },
                { PaymentType.Reserved2,    "10" }
            };
            ServiceOptions.RemapPaymentTypes(Info.SerialNumber, paymentTypeMappings);
            return paymentTypeMappings;
        }

        public override DeviceStatusWithDateTime CheckStatus()
        {
            var (dateTime, status) = GetDateTime();
            var statusEx = new DeviceStatusWithDateTime(status);
            if (dateTime.HasValue)
            {
                statusEx.DeviceDateTime = dateTime.Value;
            }
            else
            {
                statusEx.AddInfo("Error occured while reading current status");
                statusEx.AddError("E409", "Cannot read current date and time");
            }
            return statusEx;
        }

        public override DeviceStatus SetDateTime(CurrentDateTime currentDateTime)
        {
            var (_, status) = SetDeviceDateTime(currentDateTime.DeviceDateTime);
            return status;
        }

        public override DeviceStatus PrintMoneyDeposit(TransferAmount transferAmount)
        {
            var (_, status) = MoneyTransfer(transferAmount);
            return status;
        }

        public override DeviceStatus PrintMoneyWithdraw(TransferAmount transferAmount)
        {
            if (transferAmount.Amount < 0m)
            {
                throw new StandardizedStatusMessageException("Withdraw amount must be positive number", "E403");
            }
            transferAmount.Amount = -transferAmount.Amount;
            var (_, status) = MoneyTransfer(transferAmount);
            return status;
        }

        protected virtual (ReceiptInfo, DeviceStatus) PrintReceiptBody(Receipt receipt)
        {
            var receiptInfo = new ReceiptInfo();

            var (fiscalMemorySerialNumber, deviceStatus) = GetFiscalMemorySerialNumber();
            if (!deviceStatus.Ok)
            {
                return (receiptInfo, deviceStatus);
            }

            receiptInfo.FiscalMemorySerialNumber = fiscalMemorySerialNumber;

            uint itemNumber = 0;
            // Receipt items
            if (receipt.Items != null) foreach (var item in receipt.Items)
                {
                    itemNumber++;
                    if (item.Type == ItemType.Comment)
                    {
                        (_, deviceStatus) = AddComment(item.Text);
                    }
                    else if (item.Type == ItemType.Sale)
                    {
                        try
                        {
                            (_, deviceStatus) = AddItem(
                                item.Department,
                                item.Text,
                                item.UnitPrice,
                                item.TaxGroup,
                                item.Quantity,
                                item.PriceModifierValue,
                                item.PriceModifierType);
                        }
                        catch (StandardizedStatusMessageException e)
                        {
                            deviceStatus = new DeviceStatus();
                            deviceStatus.AddError(e.Code, e.Message);
                        }
                    }
                    if (!deviceStatus.Ok)
                    {
                        AbortReceipt();
                        deviceStatus.AddInfo($"Error occurred in Item {itemNumber}");
                        return (receiptInfo, deviceStatus);
                    }
                    else if (item.Type == ItemType.SurchargeAmount)
                    {
                        (_, deviceStatus) = SubtotalChangeAmount(item.Amount);
                    }
                    else if (item.Type == ItemType.DiscountAmount)
                    {
                        (_, deviceStatus) = SubtotalChangeAmount(-item.Amount);
                    }
                }

            // Receipt payments
            if (receipt.Payments == null || receipt.Payments.Count == 0)
            {
                (_, deviceStatus) = FullPaymentAndCloseReceipt();
                if (!deviceStatus.Ok)
                {
                    AbortReceipt();
                    deviceStatus.AddInfo($"Error occurred while making full payment in cash and closing the receipt");
                    return (receiptInfo, deviceStatus);
                }
            }
            else
            {
                uint paymentNumber = 0;
                foreach (var payment in receipt.Payments)
                {
                    paymentNumber++;

                    if (payment.PaymentType == PaymentType.Change)
                    {
                        continue;
                    }

                    try
                    {
                        (_, deviceStatus) = AddPayment(payment.Amount, payment.PaymentType);
                    }
                    catch (StandardizedStatusMessageException e)
                    {
                        deviceStatus = new DeviceStatus();
                        deviceStatus.AddError(e.Code, e.Message);
                    }

                    if (!deviceStatus.Ok)
                    {
                        AbortReceipt();
                        deviceStatus.AddInfo($"Error occurred in Payment {paymentNumber}");
                        return (receiptInfo, deviceStatus);
                    }
                }

                itemNumber = 0;
                if (receipt.Items != null) foreach (var item in receipt.Items)
                    {
                        itemNumber++;
                        if (item.Type == ItemType.FooterComment)
                        {
                            (_, deviceStatus) = AddComment(item.Text);
                            if (!deviceStatus.Ok)
                            {
                                deviceStatus.AddInfo($"Error occurred in Item {itemNumber}");
                                return (receiptInfo, deviceStatus);
                            }
                        }
                    }

                (_, deviceStatus) = CloseReceipt();
                if (!deviceStatus.Ok)
                {
                    AbortReceipt();
                    deviceStatus.AddInfo($"Error occurred while closing the receipt");
                    return (receiptInfo, deviceStatus);
                }
            }

            return GetLastReceiptInfo();
        }

        public override (ReceiptInfo, DeviceStatus) PrintReversalReceipt(ReversalReceipt reversalReceipt)
        {
            // Abort all unfinished or erroneus receipts
            AbortReceipt();

            // Receipt header
            var (_, deviceStatus) = OpenReversalReceipt(
                reversalReceipt.Reason,
                reversalReceipt.ReceiptNumber,
                reversalReceipt.ReceiptDateTime,
                reversalReceipt.FiscalMemorySerialNumber,
                reversalReceipt.UniqueSaleNumber,
                reversalReceipt.Operator,
                reversalReceipt.OperatorPassword);
            if (!deviceStatus.Ok)
            {
                AbortReceipt();
                deviceStatus.AddInfo($"Error occured while opening new fiscal reversal receipt");
                return (new ReceiptInfo(), deviceStatus);
            }

            ReceiptInfo receiptInfo;
            (receiptInfo, deviceStatus) = PrintReceiptBody(reversalReceipt);
            if (!deviceStatus.Ok)
            {
                AbortReceipt();
                deviceStatus.AddInfo($"Error occured while printing receipt items");
            }

            return (receiptInfo, deviceStatus);
        } 

        public override (ReceiptInfo, DeviceStatus) PrintReceipt(Receipt receipt)
        {
            // Abort all unfinished or erroneus receipts
            AbortReceipt();

            // Receipt header
            var (_, deviceStatus) = OpenReceipt(
                receipt.UniqueSaleNumber,
                receipt.Operator,
                receipt.OperatorPassword
            );
            if (!deviceStatus.Ok)
            {
                AbortReceipt();
                deviceStatus.AddInfo($"Error occured while opening new fiscal receipt");
                return (new ReceiptInfo(), deviceStatus);
            }

            ReceiptInfo receiptInfo;
            (receiptInfo, deviceStatus) = PrintReceiptBody(receipt);
            if (!deviceStatus.Ok)
            {
                AbortReceipt();
                deviceStatus.AddInfo($"Error occured while printing receipt items");
            }

            return (receiptInfo, deviceStatus);
        }

        protected virtual (ReceiptInfo, DeviceStatus) GetLastReceiptInfo()
        {
            // QR Code Data Format: <FM Number>*<Receipt Number>*<Receipt Date>*<Receipt Hour>*<Receipt Amount>
            // 50163145*000002*2020-01-28*15:29:00*30.00
            var (qrCodeData, deviceStatus) = GetLastReceiptQRCodeData();
            if (!deviceStatus.Ok)
            {
                deviceStatus.AddInfo($"Error occurred while reading last receipt QR code data");
                return (new ReceiptInfo(), deviceStatus);
            }

            var qrCodeFields = qrCodeData.Split('*');
            decimal receiptAmount;
            try
            {
                receiptAmount = decimal.Parse(qrCodeFields[4], CultureInfo.InvariantCulture);
            }
            catch
            {
                deviceStatus.AddInfo($"Error occurred while parsing last receipt QR code data (receipt amount)");
                return (new ReceiptInfo(), deviceStatus);
            }
            DateTime receiptDateTime;
            try
            {
                receiptDateTime = DateTime.ParseExact(string.Format(
                    $"{qrCodeFields[2]} {qrCodeFields[3]}"),
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                deviceStatus.AddInfo($"Error occurred while parsing last receipt QR code data (receipt date and time)");
                return (new ReceiptInfo(), deviceStatus);
            }
            string receiptNumber = qrCodeFields[1];
            if (String.IsNullOrWhiteSpace(receiptNumber))
            {
                deviceStatus.AddInfo($"Error occurred while reading last receipt number");
                deviceStatus.AddError("E409", $"Last receipt number is empty");
                return (new ReceiptInfo(), deviceStatus);
            }
            return (new ReceiptInfo
            {
                FiscalMemorySerialNumber = qrCodeFields[0],
                ReceiptAmount = receiptAmount,
                ReceiptNumber = receiptNumber,
                ReceiptDateTime = receiptDateTime
            }, deviceStatus);
        }

        public override DeviceStatusWithCashAmount Cash(Credentials credentials)
        {
            var (response, status) = Request(CommandReadDailyAvailableAmounts, "0");
            var statusEx = new DeviceStatusWithCashAmount(status);
            var commaFields = response.Split(';');
            if (commaFields.Length < 3)
            {
                statusEx.AddInfo("Error occured while reading cash amount");
                statusEx.AddError("E409", "Invalid format");
            }
            else
            {
                var amountString = commaFields[1].Trim();
                if (amountString.Contains("."))
                {
                    statusEx.Amount = decimal.Parse(amountString, CultureInfo.InvariantCulture);
                }
                else
                {
                    statusEx.Amount = decimal.Parse(amountString, CultureInfo.InvariantCulture) / 100m;
                }
            }
            return statusEx;
        }

        public override DeviceStatus PrintZReport(Credentials credentials)
        {
            var (_, status) = PrintDailyReport(true);
            return status;
        }

        public override DeviceStatus PrintXReport(Credentials credentials)
        {
            var (_, status) = PrintDailyReport(false);
            return status;
        }

        public override DeviceStatus PrintDuplicate(Credentials credentials)
        {
            var (_, status) = Request(CommandPrintLastReceiptDuplicate);
            return status;
        }

        public override DeviceStatusWithDateTime Reset(Credentials credentials)
        {
            AbortReceipt();
            FullPaymentAndCloseReceipt();
            return CheckStatus();
        }

        public override DeviceStatus PrintFiscalReport(FiscalReport fiscalReport)
        {
            var (_, deviceStatus) = PrintReportForDate(
                fiscalReport.StartDate,
                fiscalReport.EndDate,
                fiscalReport.Type
            );

            if (!deviceStatus.Ok)
            {
                deviceStatus.AddInfo($"Error occured while printing monthly report");
                return deviceStatus;
            }

            return deviceStatus;
        }
    }
}
