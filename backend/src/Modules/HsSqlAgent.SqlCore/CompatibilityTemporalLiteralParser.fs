#nowarn "3261" "3262"

namespace HsSqlAgent.SqlCore.SqlParsing

open System
open System.Globalization
open System.Runtime.InteropServices
open HsSqlAgent.SqlCore.Models

[<AbstractClass; Sealed>]
type SqlTemporalLiteralParser private () =
    static let timeFormats =
        [| "HH:mm"; "HH:mm:ss"; "HH:mm:ss.FFFFFFF" |]

    static let localTimestampFormats =
        [| "yyyy-MM-dd HH:mm"
           "yyyy-MM-dd HH:mm:ss"
           "yyyy-MM-dd HH:mm:ss.FFFFFFF"
           "yyyy-MM-dd'T'HH:mm"
           "yyyy-MM-dd'T'HH:mm:ss"
           "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF" |]

    static let offsetTimestampFormats =
        [| "yyyy-MM-dd HH:mmzzz"
           "yyyy-MM-dd HH:mm:sszzz"
           "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz"
           "yyyy-MM-dd'T'HH:mmzzz"
           "yyyy-MM-dd'T'HH:mm:sszzz"
           "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz" |]

    static member TryParseDate(value: string, [<Out>] date: byref<SqlDateValue>) =
        let mutable parsed = DateOnly.MinValue
        if DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            &parsed) then
            date <- SqlDateValue(parsed)
            true
        else
            date <- Unchecked.defaultof<SqlDateValue>
            false

    static member TryParseTime(value: string, [<Out>] time: byref<SqlTimeValue>) =
        let mutable parsed = TimeOnly.MinValue
        if TimeOnly.TryParseExact(
            value,
            timeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            &parsed) then
            time <- SqlTimeValue(parsed)
            true
        else
            time <- Unchecked.defaultof<SqlTimeValue>
            false

    static member private HasExplicitOffset(value: string) =
        if value.EndsWith('Z') then true
        else
            let separator = max (value.LastIndexOf('T')) (value.LastIndexOf(' '))
            separator >= 0
            && (value.LastIndexOf('+') > separator || value.LastIndexOf('-') > separator)

    static member TryParseTimestamp(value: string, [<Out>] timestamp: byref<SqlTemporalValue>) =
        let mutable local = DateTime.MinValue
        let mutable offset = DateTimeOffset.MinValue

        if value.EndsWith('Z')
           && DateTime.TryParseExact(
                value.Substring(0, value.Length - 1),
                localTimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                &local) then
            timestamp <-
                SqlOffsetDateTimeValue(
                    DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero))
                :> SqlTemporalValue
            true
        elif SqlTemporalLiteralParser.HasExplicitOffset(value)
             && DateTimeOffset.TryParseExact(
                value,
                offsetTimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                &offset) then
            timestamp <- SqlOffsetDateTimeValue(offset) :> SqlTemporalValue
            true
        elif DateTime.TryParseExact(
                value,
                localTimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                &local) then
            timestamp <- SqlLocalDateTimeValue(local) :> SqlTemporalValue
            true
        else
            timestamp <- Unchecked.defaultof<SqlTemporalValue>
            false
