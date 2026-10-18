// Dumps enum value -> name from Nival "enum to string" switch functions
// (CMP EDX,max; JA default; JMP [EDX*4 + table]; each case pushes a string).
// Usage: -postScript DumpEnum.java <outFile> <funcAddr> [funcAddr...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.*;
import java.io.*;

public class DumpEnum extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Listing l = currentProgram.getListing();
        for (int i = 1; i < a.length; i++) {
            Address f = toAddr(a[i]);
            long max = -1; Address table = null;
            Instruction ins = l.getInstructionAt(f);
            for (int k = 0; k < 12 && ins != null; k++, ins = ins.getNext()) {
                String m = ins.getMnemonicString();
                if (m.equals("CMP") && ins.getOpObjects(1).length == 1 && ins.getOpObjects(1)[0] instanceof Scalar s) max = s.getUnsignedValue();
                if (m.equals("JMP") && ins.getNumOperands() == 1) {
                    for (Object o : ins.getOpObjects(0)) if (o instanceof Scalar s && s.getUnsignedValue() > 0x400000) table = toAddr(s.getUnsignedValue());
                }
            }
            out.println("ENUM " + a[i] + " max=" + max + " table=" + table);
            if (table == null) continue;
            for (long v = 0; v <= max; v++) {
                Address target = toAddr(getInt(table.add(v * 4)) & 0xffffffffL);
                String name = "?";
                Instruction c = l.getInstructionAt(target);
                if (c == null) { disassemble(target); c = l.getInstructionAt(target); }
                for (int k = 0; k < 4 && c != null; k++, c = c.getNext()) {
                    for (Reference r : c.getReferencesFrom()) {
                        Data d = l.getDataAt(r.getToAddress());
                        if (d != null && d.hasStringValue()) { name = String.valueOf(d.getValue()); break; }
                        try {
                            byte[] b = new byte[96]; currentProgram.getMemory().getBytes(r.getToAddress(), b);
                            int n = 0; while (n < b.length && b[n] >= 32 && b[n] < 127) n++;
                            if (n > 3 && n < b.length && b[n] == 0) { name = new String(b, 0, n, "US-ASCII"); break; }
                        } catch (Exception e) {}
                    }
                    if (!name.equals("?")) break;
                }
                out.println(v + " 0x" + Long.toHexString(v) + " " + name);
            }
        }
        out.close();
    }
}
