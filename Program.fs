open System;
open System.Collections.Generic
open System.Diagnostics
open System.IO;
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml
open SkiaSharp
open Tweetinvi
open Tweetinvi.Parameters

let exec fileName args = 
    let startInfo = ProcessStartInfo(UseShellExecute = false, CreateNoWindow = false, RedirectStandardOutput = true, FileName = fileName, Arguments = args)
    use proc = new Process(StartInfo = startInfo)

    proc.Start() |> ignore

    let output = proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask

    proc.WaitForExit()
    output

let wrap (text : string) (maxWidth : float32) (measure: string -> float32) =
    let spaceWidth = measure " "
    text.Split " "
        |> Array.toList
        |> fun words -> ("", words)
        ||> List.fold (fun text word -> 
            let line = text.Split Environment.NewLine |> (Array.rev >> Array.head)
            let wrapped = match measure line + measure word + spaceWidth with
                          | w when w >= maxWidth -> Environment.NewLine + word
                          | _ -> word

            match String.IsNullOrWhiteSpace text with
            | true -> wrapped
            | _ -> String.concat " " [text; wrapped]) 
            
[<EntryPoint>]
let main argv =
    match Array.toList argv with
    | [channelName; exclude] ->
        printfn "Selecting video from \"%s\"" channelName
        
        let (dir, videos) = 
            Path.GetTempPath()
            |> fun temp -> Path.Combine(temp, "reviewbrahbot", channelName)
            |> fun dir -> (dir, Path.Combine(dir, "videos.json"))
        
        printfn "Working directory \"%s\"" dir

        let (|Valid|Expired|) file = 
            match file with
            | f when File.Exists f && File.GetLastWriteTime f > DateTime.Now.AddDays(-1.) -> Valid
            | _ -> Expired
            
        let (title, url) =
            async {
                let! json = match videos with
                            | Valid -> File.ReadAllTextAsync(videos) |> Async.AwaitTask
                            | Expired -> sprintf "-J --flat-playlist https://youtube.com/user/%s" channelName |> exec "youtube-dl"


                Directory.CreateDirectory(dir) |> ignore                
                do! File.WriteAllTextAsync(videos, json) |> Async.AwaitTask

                return json
            }
            |> Async.RunSynchronously
            |> JsonDocument.Parse
            |> fun doc -> doc.RootElement.GetProperty("entries")
            |> ((fun entries -> entries.EnumerateArray()) >> Seq.toList)
            |> List.filter (fun entry -> not <| Regex.IsMatch((entry.GetProperty("title").GetString()), exclude))
            |> fun entries -> entries.[Random().Next(entries.Length)]
            |> fun entry -> (entry.GetProperty("title").GetString(), entry.GetProperty("url").GetString())

        printfn "Creating screenshot for \"%s\"" title 

        let (mp4, srv3, jpg) =
            (Path.Combine(dir, Guid.NewGuid().ToString()), ["mp4"; "en.srv3"; "jpg"])
            |> fun (out, exts) -> 
                exts 
                |> List.map (fun ext -> sprintf "%s.%s" out ext) 
                |> fun list -> (list.[0], list.[1], list.[2])

        async { return! sprintf "--output %s --sub-format srv3 --write-auto-sub --format best --no-progress --no-check-certificate %s" mp4 url |> exec "youtube-dl" }
        |> Async.RunSynchronously            
        |> ignore
    
        let timestamp = 
            async { return! sprintf "-i %s -v quiet -print_format json -show_format -show_streams -hide_banner -loglevel quiet" mp4 |> exec "ffprobe" }
            |> Async.RunSynchronously 
            |> JsonDocument.Parse
            |> fun doc -> doc.RootElement.GetProperty("format").GetProperty("duration").GetString()
            |> Double.Parse 
            |> fun secs -> Random().NextDouble() * (secs - 0.) + 0.
            |> TimeSpan.FromSeconds

        async { return! sprintf "-ss %s -i %s -vframes 1 -q:v 2 %s -loglevel quiet" (timestamp.ToString("hh\\:mm\\:ss")) mp4 jpg |> exec "ffmpeg" }
        |> Async.RunSynchronously            
        |> ignore

        let caption =
            async { 
                return match srv3 with
                       | file when File.Exists file  -> 
                            let doc = XmlDocument()
                            doc.Load srv3
                            doc.SelectNodes "//p" 
                            |> Seq.cast<XmlNode>
                            |> Seq.map (fun node -> 
                                (Double.Parse(node.Attributes.GetNamedItem("t").Value), node.ChildNodes
                                |> Seq.cast<XmlNode>
                                |> Seq.map (fun child -> child.InnerText.Trim())
                                |> (fun text -> String.concat " " text)))
                            |> Seq.tryFind(fun (t, c) -> not <| String.IsNullOrWhiteSpace c && timestamp.TotalMilliseconds >= t - 2000. && timestamp.TotalMilliseconds <= t + 2000.)
                            |> function  
                               | Some (_, text) -> Some text
                               | None -> None
                        | _ -> None                  
            } |> Async.RunSynchronously            

 
        async {
            match caption with
            | Some caption ->
                use img = File.OpenRead jpg
                use bitmap = SKBitmap.Decode(img)
                use canvas = new SKCanvas(bitmap)

                let width = float32(bitmap.Width)
                let height = float32(bitmap.Height)

                use captionPaint = new SKPaint(TextSize = height * 0.05f, Color = SKColors.White, IsAntialias = true, Typeface = SKTypeface.FromFile "roboto.ttf")
                use captionBgPaint = new SKPaint(Style = SKPaintStyle.Fill, Color = SKColor.Parse("#c2000000"), IsAntialias = true)

                wrap caption (width * 0.9f) captionPaint.MeasureText
                |> (fun text -> text.Split Environment.NewLine)
                |> (Array.toList >> List.rev)
                |> List.iteri (fun i line ->
                    printfn "%s" line
                    let mutable rect = SKRect()
                    captionPaint.MeasureText(line, &rect) |> ignore
                    rect.Width = MathF.Min(rect.Width, width) |> ignore

                    let frame = rect
                    frame.Inflate(6.f, 6.f)

                    let x = (width - frame.Width) / 2.f
                    let y = height - height * 0.04f - frame.Height * float32(i)
                    frame.Offset(x, y)

                    canvas.DrawRoundRect(frame, 3.f, 3.f, captionBgPaint)
                    canvas.DrawText(line, x, y, captionPaint))

                use captioned = SKImage.FromBitmap bitmap
                File.WriteAllBytes(jpg, captioned.Encode().ToArray())
                
                printfn "Written image to disk"
            | None -> ()                            
        } |> Async.RunSynchronously |> ignore
        
        printfn "Tweeting image"

        let (consumerKey, consumerSecret, userAccessToken, userAccessSecret) = (
            Environment.GetEnvironmentVariable("TWITTER_CONSUMER_KEY"),
            Environment.GetEnvironmentVariable("TWITTER_CONSUMER_SECRET"),
            Environment.GetEnvironmentVariable("TWITTER_USERACCESS_KEY"),
            Environment.GetEnvironmentVariable("TWITTER_USERACCESS_SECRET")
        )

        let media = List<byte[]>([File.ReadAllBytes jpg])
        Auth.SetUserCredentials(consumerKey, consumerSecret, userAccessToken, userAccessSecret) |> ignore
        let tweet = Tweet.PublishTweet(" ", PublishTweetOptionalParameters(MediaBinaries = media))       
        
        printfn "Tweeted image to \"%s\"" tweet.Url

        0
    | _ -> 
        eprintfn "Usage: [channelNames] [exclude]"
        0