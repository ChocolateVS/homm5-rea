// Lists functions containing indirect calls through all of the given vtable slots.  args: <out> <hexSlot>...
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.*;
import java.util.*;
public class FindVSlots extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Set<String> want = new HashSet<>();
        for (int i = 1; i < a.length; i++) want.add("0x" + Long.toHexString(Long.parseLong(a[i].replace("0x",""),16)));
        FunctionIterator it = currentProgram.getFunctionManager().getFunctions(true);
        while (it.hasNext()) {
            Function f = it.next();
            Set<String> seen = new HashSet<>();
            InstructionIterator ii = currentProgram.getListing().getInstructions(f.getBody(), true);
            while (ii.hasNext()) {
                Instruction ins = ii.next();
                if (!ins.getMnemonicString().equals("CALL")) continue;
                String s = ins.toString();
                int p = s.indexOf("+ 0x");
                if (s.contains("dword ptr [") && p > 0) {
                    String slot = s.substring(p + 2, s.indexOf(']', p)).trim();
                    if (want.contains(slot)) seen.add(slot);
                }
            }
            if (seen.size() == want.size()) out.println(f.getEntryPoint() + " " + f.getName());
        }
        out.close();
    }
}
