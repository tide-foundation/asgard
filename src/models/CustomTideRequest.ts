import { StringFromUint8Array, StringToUint8Array } from "../utils/Serialization";
import { TideMemory } from "../utils/TideMemory";
import BaseTideRequest from "./TideRequest";

export default class CustomTideRequest extends BaseTideRequest {
    customInfo: CustomInfo | undefined;
    datasToSign: Uint8Array[] = [];
    constructor(name: string, version: string, authFlow: string, draft: Uint8Array, dyanmicData: Uint8Array) {
        super(name, version, authFlow, draft, dyanmicData);
        if(draft.length > 0) this.datasToSign.push(draft); // in case they only want to sign one thing
    }
    addDataToSign(data: Uint8Array){
        this.datasToSign.push(data);
        this.draft = TideMemory.CreateFromArray([StringToUint8Array(JSON.stringify(this.customInfo)), ... this.datasToSign]); // recreate draft on change
        return this;
    }
    setHumanReadableName(n: string){
        if(!this.customInfo){
            this.customInfo = {
                humanReadableName: n,
                additionalInfo: null
            }
        }else{
            this.customInfo["humanReadableName"] = n;
        }
        this.draft = TideMemory.CreateFromArray([StringToUint8Array(JSON.stringify(this.customInfo)), ... this.datasToSign]); // recreate draft on change
        return this;
    }
    setAdditionalInfo(info: any){
        if(!this.customInfo){
            this.customInfo = {
                humanReadableName: "",
                additionalInfo: info
            }
        }else{
            this.customInfo["additionalInfo"] = info;
        }
        this.draft = TideMemory.CreateFromArray([StringToUint8Array(JSON.stringify(this.customInfo)), ... this.datasToSign]); // recreate draft on change
        return this;
    }
    getAdditionalInfoSupplied(): any{
        if(this.draft.length > 0) return JSON.parse(StringFromUint8Array(this.draft.GetValue(0)))["additionalInfo"];
        else return null;
    }
}
interface CustomInfo{
    humanReadableName: string;
    additionalInfo: any;
}