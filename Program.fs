#if INTERACTIVE
#load ".paket/load/netcoreapp2.2/main.group.fsx"
#load "Window.fs"
#endif


open System
open System.IO.MemoryMappedFiles
open System.IO
open System.Runtime.InteropServices
open FSharp.NativeInterop
open Game
open Microsoft.Xna.Framework.Graphics
open System.Buffers.Binary
open System.Runtime.CompilerServices
open MemoryMappedFiles
open Diagnostics
open Argu
#nowarn "9"


[<STAThread; EntryPoint>]
let Main args =

    let filename = Args.commandLine.Path
    
    let width = 640
    let height = 480
    let state = ()
    let render state _ _ = state

    use win = new Window<uint16,_>(int width, int height, SurfaceFormat.Bgr565, state, render, Args.commandLine.Fps)
    //use listener = new EventListener()
    GC.Collect()
    win.Run()
    0

