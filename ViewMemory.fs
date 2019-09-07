module MemoryMappedFiles
open System
open System.Runtime.InteropServices
open FSharp.NativeInterop
#nowarn "9"


[<Struct>]
type ViewMemory<'t> = {
        Handle: SafeBuffer
        offset: int
        length: int
} with
        member this.IsEmpty = this.length = 0
        member this.Span() : 't ReadOnlySpan=
                let p = NativePtr.ofNativeInt<byte> (this.Handle.DangerousGetHandle())
                let start = NativePtr.add p this.offset
                
                new ReadOnlySpan<'t>(NativePtr.toVoidPtr start, this.length)
        
        member this.Slice(offset) : ViewMemory<'t> =
                let newOffset = this.offset + offset
                if offset < this.length then
                    { Handle = this.Handle
                      offset = newOffset
                      length = this.length - offset }
                else
                    { Handle = this.Handle
                      offset = this.offset + this.length
                      length = 0 }

        member this.Slice(offset, length) : ViewMemory<'t> =
                let offset = min (this.offset + offset) this.length
                let length = min (this.length - offset) length
                { Handle = this.Handle
                  offset = offset 
                  length = length }
                        
type System.IO.MemoryMappedFiles.MemoryMappedViewAccessor with
        member this.AsMemory<'t>(offset, length) =
                { Handle = this.SafeMemoryMappedViewHandle
                  offset = offset
                  length = length //(int this.Capacity - offset)
                } : ViewMemory<'t>
        member this.AsMemory() =
                this.AsMemory(0, int this.Capacity)
        member this.AsMemory(offset) =
                this.AsMemory(offset, int this.Capacity)
