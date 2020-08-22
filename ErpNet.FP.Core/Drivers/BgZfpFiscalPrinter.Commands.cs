﻿namespace ErpNet.FP.Core.Drivers
{
    using System;
    using System.Globalization;
    using System.Text;

    public partial class BgZfpFiscalPrinter : BgFiscalPrinter
    {
        protected const byte
            CommandReadFDNumbers = 0x60,
            CommandGetStatus = 0x20,
            CommandVersion = 0x21,
            CommandPrintDailyFiscalReport = 0x7c,
            CommandNoFiscalRAorPOAmount = 0x3b,
            CommandOpenReceipt = 0x30,
            CommandCloseReceipt = 0x38,
            CommandFullPaymentAndCloseReceipt = 0x36,
            CommandAbortReceipt = 0x39,
            CommandSellCorrection = 0x31,
            CommandSellCorrectionDepartment = 0x34,
            CommandFreeText = 0x37,
            CommandPayment = 0x35,
            CommandGetDateTime = 0x68,
            CommandSetDateTime = 0x48,
            CommandSubtotal = 0x33,
            CommandReadLastReceiptQRCodeData  = 0x72,
            CommandGetTaxIdentificationNumber  = 0x61,
            CommandReadDailyAvailableAmounts  = 0x6E,
            CommandPrintLastReceiptDuplicate  = 0x3A,
            CommandPrintBriefReportForDate    = 0x7b,
            CommandPrintDetailedReportForDate = 0x7a,
            CommandGSCommand = 0x1d;
        protected const byte
            // Protocol: 36 symbols for article's name. 34 symbols are printed on paper.
            // Attention: ItemText should be padded right with spaces until reaches mandatory 
            // length of 36 symbols. Otherwise we will have syntax error!
            ItemTextMandatoryLength = 36;

        public virtual (string, DeviceStatus) GetStatus()
        {
            var (deviceStatus, _ /* ignore commandStatus */) = ParseResponseAsByteArray(RawRequest(CommandGetStatus, null));
            return ("", ParseStatus(deviceStatus));
        }

        public virtual (string, DeviceStatus) GetTaxIdentificationNumber()
        {
            var (response, deviceStatus) = Request(CommandGetTaxIdentificationNumber);
            var commaFields = response.Split(';');
            if (commaFields.Length > 1)
            {
                return (commaFields[0].Trim(), deviceStatus);
            }
            return (string.Empty, deviceStatus);
        }

        public virtual (string, DeviceStatus) SubtotalChangeAmount(Decimal amount)
        {
            // <OptionPrinting[1]> <;> <OptionDisplay[1]> {<':'> <DiscAddV[1..8]>} {<','>< DiscAddP[1..7] >}
            return Request(CommandSubtotal, $"1;0:{amount.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        public virtual (string, DeviceStatus) GetLastReceiptQRCodeData()
        {
            return Request(CommandReadLastReceiptQRCodeData, "B");
        }

        public virtual (string, DeviceStatus) GetFiscalMemorySerialNumber()
        {
            var (rawDeviceInfo, deviceStatus) = GetRawDeviceInfo();
            var fields = rawDeviceInfo.Split(";");
            if (fields != null && fields.Length > 0)
            {
                return (fields[^1], deviceStatus);
            }
            else
            {
                deviceStatus.AddInfo($"Error occured while reading device info");
                deviceStatus.AddError("E409", $"Wrong number of fields");
                return (string.Empty, deviceStatus);
            }
        }

        public virtual (DateTime?, DeviceStatus) GetDateTime()
        {
            var (dateTimeResponse, deviceStatus) = Request(CommandGetDateTime);
            if (!deviceStatus.Ok)
            {
                deviceStatus.AddInfo($"Error occured while reading current date and time");
                return (null, deviceStatus);
            }

            try
            {
                var dateTime = DateTime.ParseExact(dateTimeResponse,
                    "dd-MM-yyyy HH:mm",
                    CultureInfo.InvariantCulture);
                return (dateTime, deviceStatus);
            }
            catch
            {
                deviceStatus.AddInfo($"Error occured while parsing current date and time");
                deviceStatus.AddError("E409", $"Wrong format of date and time");
                return (null, deviceStatus);
            }
        }

        public virtual (string, DeviceStatus) SetDeviceDateTime(DateTime dateTime)
        {
            return Request(CommandSetDateTime, dateTime.ToString("dd-MM-yy HH:mm:ss", CultureInfo.InvariantCulture));
        }

        public virtual (string, DeviceStatus) MoneyTransfer(TransferAmount transferAmount)
        {
            return Request(CommandNoFiscalRAorPOAmount, string.Join(";", new string[]
            {
                String.IsNullOrEmpty(transferAmount.Operator) ?
                    Options.ValueOrDefault("Operator.ID", "1")
                    :
                    transferAmount.Operator,
                String.IsNullOrEmpty(transferAmount.OperatorPassword) ?
                    Options.ValueOrDefault("Operator.Password", "0000")
                    :
                    transferAmount.OperatorPassword,
                "0", // Protocol: Reserved 
                transferAmount.Amount.ToString("F2", CultureInfo.InvariantCulture)
            }));
        }

        public virtual (string, DeviceStatus) OpenReceipt(
            string uniqueSaleNumber,
            string operatorId,
            string operatorPassword)
        {
            // Protocol: <OperNum[1..2]> <;> <OperPass[6]> <;> <ReceiptFormat[1]> <;> 
            //           <PrintVAT[1]> <;> <FiscalRcpPrintType[1]> {<’$’> <UniqueReceiptNumber[24]>}
            return Request(CommandOpenReceipt, string.Join(";", new string[] {
                String.IsNullOrEmpty(operatorId) ?
                    Options.ValueOrDefault("Operator.ID", "1")
                    :
                    operatorId,
                String.IsNullOrEmpty(operatorPassword) ?
                    Options.ValueOrDefault("Operator.Password", "0000")
                    :
                    operatorPassword,
                "1", // Protocol: Detailed
                "1", // Protocol: Include VAT
                "2$"+uniqueSaleNumber, // Protocol: Buffered printing (4) is faster than (2) postponed printing, 
                // but there are problems with read timeout because 
                // the Fiscal Device becomes non-responsable when 
                // there are many rows to be printed.
                // Delimiter '$' before USN.
            }));
        }

        public virtual (string, DeviceStatus) OpenReversalReceipt(
            ReversalReason reason,
            string receiptNumber,
            System.DateTime receiptDateTime,
            string fiscalMemorySerialNumber,
            string uniqueSaleNumber,
            string operatorId,
            string operatorPassword)
        {
            // Protocol: <OperNum[1..2]> <;> <OperPass[6]> <;> <ReceiptFormat[1]> <;>
            //            < PrintVAT[1] > <;> < StornoRcpPrintType[1] > <;> < StornoReason[1] > <;>
            //            < RelatedToRcpNum[1..6] > <;> < RelatedToRcpDateTime ”DD-MM-YY HH:MM[:SS]”> <;>
            //            < FMNum[8] > {<;> < RelatedToURN[24] >}            
            return Request(CommandOpenReceipt, string.Join(";", new string[] {
                String.IsNullOrEmpty(operatorId) ?
                    Options.ValueOrDefault("Operator.ID", "1")
                    :
                    operatorId,
                String.IsNullOrEmpty(operatorPassword) ?
                    Options.ValueOrDefault("Operator.Password", "0000")
                    :
                    operatorPassword,
                "1", // Protocol: Detailed
                "1", // Protocol: Include VAT
                "D", // Protocol: Buffered printing
                GetReversalReasonText(reason),
                receiptNumber,
                receiptDateTime.ToString("dd-MM-yy HH:mm:ss", CultureInfo.InvariantCulture),
                fiscalMemorySerialNumber,
                uniqueSaleNumber
            }));
        }

        public virtual (string, DeviceStatus) AddItem(
            int department,
            string itemText,
            decimal unitPrice,
            TaxGroup taxGroup,
            decimal quantity = 0,
            decimal priceModifierValue = 0,
            PriceModifierType priceModifierType = PriceModifierType.None)
        {
            var itemData = new StringBuilder();
            if (department <= 0) 
            {
                // Protocol: <NamePLU[36]><;><OptionVATClass[1]><;><Price[1..10]>{<'*'>< Quantity[1..10]>}
                //           {<','><DiscAddP[1..7]>}{<':'><DiscAddV[1..8]>}
                itemData
                    .Append(itemText.WithMaxLength(Info.ItemTextMaxLength).PadRight(ItemTextMandatoryLength))
                    .Append(';')
                    .Append(GetTaxGroupText(taxGroup))
                    .Append(';')
                    .Append(unitPrice.ToString("F2", CultureInfo.InvariantCulture));
            }
            else
            {
                // Protocol: <NamePLU[36]><;><DepNum[1..2]><;><Price[1..10]>{<'*'>< Quantity[1..10]>}
                //           {<','><DiscAddP[1..7]>}{<':'><DiscAddV[1..8]>}
                itemData
                    .Append(itemText.WithMaxLength(Info.ItemTextMaxLength).PadRight(ItemTextMandatoryLength))
                    .Append(';')
                    .Append((department + 0x80).ToString("X2"))
                    .Append(';')
                    .Append(unitPrice.ToString("F2", CultureInfo.InvariantCulture));
            }

            if (quantity != 0)
            {
                itemData
                    .Append('*')
                    .Append(quantity.ToString(CultureInfo.InvariantCulture));
            }
            switch (priceModifierType)
            {
                case PriceModifierType.DiscountPercent:
                    itemData
                        .Append(',')
                        .Append((-priceModifierValue).ToString("F2", CultureInfo.InvariantCulture));
                    break;
                case PriceModifierType.DiscountAmount:
                    itemData
                        .Append(':')
                        .Append((-priceModifierValue).ToString("F2", CultureInfo.InvariantCulture));
                    break;
                case PriceModifierType.SurchargePercent:
                    itemData
                        .Append(',')
                        .Append(priceModifierValue.ToString("F2", CultureInfo.InvariantCulture));
                    break;
                case PriceModifierType.SurchargeAmount:
                    itemData
                        .Append(':')
                        .Append(priceModifierValue.ToString("F2", CultureInfo.InvariantCulture));
                    break;
                default:
                    break;
            }

            if (department <= 0) 
            {
                return Request(CommandSellCorrection, itemData.ToString());
            }
            else
            {
                return Request(CommandSellCorrectionDepartment, itemData.ToString());
            }
        }

        public virtual (string, DeviceStatus) AddComment(string text)
        {
            return Request(CommandFreeText, text.WithMaxLength(Info.CommentTextMaxLength));
        }

        public virtual (string, DeviceStatus) CloseReceipt()
        {
            return Request(CommandCloseReceipt);
        }

        public virtual (string, DeviceStatus) AbortReceipt()
        {
            return Request(CommandAbortReceipt);
        }

        public virtual (string, DeviceStatus) FullPaymentAndCloseReceipt()
        {
            return Request(CommandFullPaymentAndCloseReceipt);
        }

        public virtual (string, DeviceStatus) AddPayment(
            decimal amount,
            PaymentType paymentType)
        {
            // Protocol: input: <PaymentType [1..2]> <;> <OptionChange [1]> <;> <Amount[1..10]> {<;><OptionChangeType[1]>}
            return Request(CommandPayment, string.Join(";", new string[] {
                GetPaymentTypeText(paymentType),
                "1", // Procotol: Without change
                amount.ToString("F2", CultureInfo.InvariantCulture)+"*"
            }));
        }

        public virtual (string, DeviceStatus) PrintDailyReport(bool zeroing = true)
        {
            if (zeroing)
            {
                return Request(CommandPrintDailyFiscalReport, "Z");
            }
            else
            {
                return Request(CommandPrintDailyFiscalReport, "X");
            }
        }

        public virtual (string, DeviceStatus) PrintReportForDate(DateTime startDate, DateTime endDate, ReportType type)
        {
            var startDateString = startDate.ToString("ddMMyy", CultureInfo.InvariantCulture);
            var endDateString = endDate.ToString("ddMMyy", CultureInfo.InvariantCulture);
            var headerData = string.Join(";", startDateString, endDateString);
            Console.WriteLine("Tremol: " + headerData);

            return Request(
                type == ReportType.Brief
                    ? CommandPrintBriefReportForDate
                    : CommandPrintDetailedReportForDate,
                headerData
            );
        }

        public virtual (string, DeviceStatus) GetRawDeviceInfo()
        {
            var (responseFD, _) = Request(CommandReadFDNumbers);
            var (responseV, deviceStatus) = Request(CommandVersion);
            return (responseV + responseFD, deviceStatus);
        }
    }
}
