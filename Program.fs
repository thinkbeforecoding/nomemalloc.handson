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

// this is a cast from ReadOnlySpan of bytes to 't 
let inline cast<'t when 't: (new: unit -> 't)
                     and  't: struct
                     and  't:> ValueType > 
                (s: byte ReadOnlySpan) =
                MemoryMarshal.Cast<byte,'t>(s)

// take the reference to the first element of type 't in the span
let inline head<'t when 't: (new: unit -> 't)
                     and  't: struct
                     and  't:> ValueType >
                (s: byte ReadOnlySpan) =
                &(cast<'t> s).[0]

// 16bits pixel type
type Pixel = uint16

[<Struct>]
type Header =
  { Size: int
    Magic: uint16
    Frames: uint16
    Width: uint16
    Height: uint16
    Depth: uint16
    Flags: uint16
    Speed: uint16 }

// The file contains two offsets at byte 80
// The first point on the first frame as Rle (Run length encoding)
// the second point on the first frame as a Delta frame
[<Struct>]
type Offsets =
  { FirstFrame: int
    RingFrame: int}


[<Struct>]
type FrameHeader =
  { FrameSize: int
    FrameType: uint16
    Chunks: uint16
    Reserved: int64 }

type ChunkType =
  | Rle = 25us
  | Delta = 27us

// The Pack=2 ensure that the size of the structure is 6..
// otherwhite the compiler will pad it to 8
[<Struct; StructLayout(LayoutKind.Sequential, Pack=2)>]
type ChunkHeader =
  { ChunkSize: int
    Type: ChunkType }


// This Ctx (read Context) struct enable us to
// manipulate two spans as a single value
// Only structures marked as IsByRefLike can contain
// other IsByRefLike structs as Span<'t>
[<Struct; IsByRefLike>]
type Ctx =
  { Chunk: byte ReadOnlySpan
    Screen: Pixel Span}

// All the functions return a new Ctx
// that is passed used data
// ctx is passed as an inref so that the value is not copied each time
module Ctx =
  // skips sizeof<'t> bytes from the source chunk span
  let inline skip<'t> (ctx: Ctx inref) =
    { ctx with Chunk = ctx.Chunk.Slice(sizeof<'t>) }

  // skips n pixels from the screen span
  let inline skipPix n (ctx: Ctx inref) =
    { ctx with Screen = ctx.Screen.Slice(n)}

  // fills size pixels of screen with pixel in chunk span
  let fill size (ctx: Ctx inref) =
    let pixel = head<Pixel> ctx.Chunk
    let dest = ctx.Screen.Slice(0,size)
    dest.Fill(pixel)
    { Chunk = ctx.Chunk.Slice(sizeof<Pixel>)
      Screen = ctx.Screen.Slice(size) }

  // copy count pixels from chunk span to screen
  let copy count (ctx: Ctx inref) =
    let pixels = (cast<Pixel> ctx.Chunk).Slice(0, count)
    pixels.CopyTo(ctx.Screen)
    { Chunk = ctx.Chunk.Slice(count * sizeof<Pixel>)
      Screen = ctx.Screen.Slice(count)}

// Run Length Encoding frame rendering
module Rle =
  // renders a single paket to screen
  let renderPaket (ctx: Ctx inref) =
    // the control is a byte that can contain
    // a number of pixels to fill or copy depending
    // on whether it is positive or negative
    let control = sbyte ctx.Chunk.[0]
    let ctx' = Ctx.skip<byte> &ctx
    if control >= 0y then
      Ctx.fill (int control) &ctx'
    else
      Ctx.copy (- (int control)) &ctx'

  // an immutable tail recursive loop to render all pakets
  let rec renderPakets pakets (ctx: Ctx inref) =
    if pakets > 0uy then
      let ctx' = renderPaket &ctx
      renderPakets (pakets-1uy) &ctx'
    else
      ctx

  // render a single line
  let renderLine (ctx: Ctx inref) =
    // the first byte contains the number of pakets
    let pakets = ctx.Chunk.[0]
    let ctx' = Ctx.skip<byte> &ctx
    renderPakets pakets &ctx'

  // an immutable tail recursive loop to render lines
  let rec renderLines height (ctx: Ctx inref) =
      if height > 0us then
        let ctx' = renderLine &ctx
        renderLines (height-1us) &ctx'

  // Renders a chunk. header is passed as inref to avoid copy
  let renderChunk (header: Header inref) (chunk: byte ReadOnlySpan) (screen: Pixel Span) =
      let ctx = { Chunk = chunk; Screen = screen }
      renderLines header.Height &ctx


// Delta frame rendering
module Delta =
  // this structs contains the two control bytes 
  // for delta pakets.
  // The skipX indicates the number or pixels to skip before
  // writing the paket data
  [<Struct>]
  type Delta =
    { SkipX: byte
      Control: sbyte }

  // this render a delta paket
  // delta paket can skip pixels when SkipX > 0
  // an be carefull, the control signe is 
  // flipped compared to rle wrt copy and fill 
  let renderPaket (ctx: Ctx inref) =
    let delta = head<Delta> ctx.Chunk
    let ctx' = Ctx.skip<Delta> &ctx 
    let ctx'' = Ctx.skipPix (int delta.SkipX) &ctx'
    let ctrl = int delta.Control
    if ctrl < 0 then
      Ctx.fill -ctrl &ctx''
    else
      Ctx.copy ctrl &ctx''

  let rec renderPakets pakets (ctx: Ctx inref) =
    if pakets > 0 then
      let ctx' = renderPaket &ctx
      renderPakets (pakets-1) &ctx'
    else
      ctx

  // renderline in delta is a bit more complicated
  // the first byte is a number of lines to skip if < 0
  // if it's >= 0 it's directly the number of pakets
  // if < 0 the number of pakets follow.
  let renderLine width (ctx: Ctx inref) =
    let control = (cast<int16>ctx.Chunk).[0]
    let ctx' = Ctx.skip<int16> &ctx
    let ctx' =
      if control < 0s then
        let skipY = int -control
        let pakets = int (cast<int16>ctx'.Chunk).[0]
        let ctx' = Ctx.skip<int16> &ctx'
        let ctx' = Ctx.skipPix (skipY * int width) &ctx'
        renderPakets pakets &ctx'
      else
        let pakets = int control
        renderPakets pakets &ctx'
    // in delta frames, pakets don't necessarily end on the end of
    // a line, so to be ready for next line, we have to skip <width>
    // pixels from previous line
    { ctx' with Screen = ctx.Screen.Slice(int width)}

  let rec renderLines width height (ctx: Ctx inref) =
      if height > 0us then
        let ctx' = renderLine width &ctx
        renderLines width (height-1us) &ctx'

  let renderChunk (header: Header inref) (chunk: byte ReadOnlySpan) (screen: Pixel Span) =
      let lines = head<uint16>(chunk)
      let ctx = { Chunk = chunk.Slice(sizeof<uint16>); Screen = screen }
      renderLines header.Width lines &ctx

let renderFrame (header: Header inref) (frame: byte ReadOnlySpan) (screen: Pixel Span) =
    let frameContent = frame.Slice(sizeof<FrameHeader>)
    let chunkHeader = head<ChunkHeader> frameContent
    let chunk = frameContent.Slice(sizeof<ChunkHeader>)
    match chunkHeader.Type with
    | ChunkType.Rle -> Rle.renderChunk &header chunk screen
    | ChunkType.Delta -> Delta.renderChunk &header chunk screen
    | _ -> ()

// this is the main render function
// we can not pass inref of span here because
// we need to pass this function as a delegate to the
// rendering window that will call it many times.
// It is not permited to use IsByRefLike types except for direct calls.
let render (header: Header) (view: byte ViewMemory) ring struct(frameNumber,offset) (screenMemory: Pixel Memory) =
  // we take the span of the file
  let fileSpan = view.Span()
  // at the current offset is the length of the frame
  let length = BinaryPrimitives.ReadInt32LittleEndian(fileSpan.Slice offset)
  // so we can slice it
  let frame = fileSpan.Slice(offset, length)

  // we also take span on memory
  let screen = screenMemory.Span

  // and we render it
  renderFrame &header  frame screen

  // if this was the last frame, we go back
  // to the ring frame offset
  // else we skip current frame length
  // to be ready to render next one
  if frameNumber = int header.Frames - 1 then
    struct(0, ring)
  else
    struct (frameNumber+1,offset + length)

[<STAThread; EntryPoint>]
let Main args =

    let filename = Args.commandLine.Path

    // we load the file in memory as a memory mapped file
    let file = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(filename, FileMode.Open)
    // the accessor gives us access to the full file bytes
    let accessor = file.CreateViewAccessor()
    let view = accessor.AsMemory<byte>()
    let span = view.Span()

    // we read the header to know width and height of the video
    let header = MemoryMarshal.Read<Header>(span)
    // the offsets to frames are at byte 80 in file
    let offsets = MemoryMarshal.Read<Offsets>(span.Slice 80)


    let width = header.Width
    let height = header.Height
    let state = struct(-1, offsets.FirstFrame)
    let render  = render header view offsets.RingFrame

    use win = new Window<uint16,_>(int width, int height, SurfaceFormat.Bgr565, state, render, Args.commandLine.Fps)
    // this listener is notified wy GC events
    // and will write them to console
    use listener = new EventListener()
    GC.Collect()

    // let's go !!
    win.Run()
    0

