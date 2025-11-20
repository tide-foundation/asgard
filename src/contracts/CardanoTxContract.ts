import { Policy } from "../models/Policy";
import { BaseContract } from "./BaseContract";
import { GenericResourceAccessThresholdRoleContract } from "./GenericResourceAccessThresholdRoleContract";



// THIS SHOULD BE IMPLEMENTED/COPIED OVER TO MECHAPURSE!!!!



export class CardanoTxContract extends GenericResourceAccessThresholdRoleContract{
    public id: string = "CardanoTx";
    protected test(policy: Policy): void {
        super.test(policy);

        // now we deserialize the draft here and check the max
        
    }
}