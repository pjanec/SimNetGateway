# Notice - vendored open-dis-csharp

The folder `Vendored/` contains the DIS 7 (IEEE 1278.1-2012) C# classes of **open-dis-csharp**, directory `CSharpDis7`,
taken at commit `681ba85` (https://github.com/open-dis/open-dis-csharp), licensed under the BSD 2-Clause "Open DIS"
licence, see `LICENSE` in this folder. The copyright headers of the files are kept.

`Vendored/Streams/` holds `DataInputStream`, `DataOutputStream`, `DataStream` and `EndianTypes` taken from
`CsharpDis6` (`OpenDis/Utilities` and `OpenDis/Core`, same repository and commit, same licence), with the namespace
changed to `DISnet.DataStreamUtilities` (CSharpDis7 references that namespace but does not contain it). Always use
`Endian.Big` on the wire.

Changes against the original (T-019, DD-006 section 7):

1. The stream classes above were added; non-ASCII bytes in their `#region` titles were replaced by spaces, CRLF -> LF.
2. `BeamAntennaPattern.cs` (`_ez`, `_ex`, `_phase`) and
   `DirectedEnergyDamage.cs` (`_temperature`): double literals changed to float literals so the code compiles.
3. `GlobalUsings.cs` (project root) supplies `System.Diagnostics.CodeAnalysis` and `System.Globalization`.
4. Simulation-management duplicate-id bug: `CreateEntityPdu`, `RemoveEntityPdu`, `StartResumePdu`, `StopFreezePdu`,
   `AcknowledgePdu`, `ActionRequestPdu` and `ActionResponsePdu` declared their own `OriginatingID` / `ReceivingID` in
   addition to the inherited `OriginatingEntityID` / `ReceivingEntityID` and marshalled both (12 bytes too many each).
   The duplicate fields were removed; `OriginatingID` / `ReceivingID` are now `[XmlIgnore]` aliases of the inherited
   properties, so the sizes are 28 / 28 / 44 / 40 / 32 / 40 as in the standard.

5. `DataInputStream.Position` (read-only) was added so the codec can check that a PDU consumed exactly its `Length` bytes.

The own code (`PduCodec`, `DisTimestamp`, `EntityAppearance`, `EntityMarkingText`, `ArticulatedPart`) lives in the
project root, namespace `SimNetGateway.Protocol.Dis7`.
