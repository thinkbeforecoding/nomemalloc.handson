module Args

open Argu

type Args =
    | [<MainCommand>]Path of string
    | Fps of int
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Path _ -> "Flh file path"
            | Fps _ -> "Frames per second"

type CommandLine =
    { Path: string
      Fps: int }

let commandLine =
    let parser = Argu.ArgumentParser.Create<Args>()
    let results = parser.ParseCommandLine()
    let fps = results.GetResult(Fps,50)
    let filename =
        let path = results.TryGetResult(Path)
        match path with
        | Some p when System.IO.File.Exists(p) ->
            p
        | _ ->
            let root = __SOURCE_DIRECTORY__
            //root + "/video/BBox.flh"
            root + "/video/Toaster.flh"
    { Path = filename
      Fps = fps }
            
