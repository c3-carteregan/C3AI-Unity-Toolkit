using System;
using UnityEngine;

public class ViewFollow : MonoBehaviour
{
   [SerializeField] Transform target;
   [SerializeField] float distance = 1f;

   private void Update()
   {

        var dist = Vector3.Distance(transform.position, target.position);
        if (dist > distance* 1.2)
        {
            JumpToPosition();
            return;
        }
        var pos = GetTargetPos();
      transform.position = Vector3.Lerp(transform.position, pos, Time.deltaTime * 2);
      transform.LookAt(target);
      transform.eulerAngles = new Vector3(0, transform.eulerAngles.y + 180, 0);
   }

    private void JumpToPosition()
    {
        var pos = GetTargetPos();
        transform.position = pos;
        transform.LookAt(target);
        transform.eulerAngles = new Vector3(0, transform.eulerAngles.y + 180, 0);
    }

    private Vector3 GetTargetPos()
    {
        Vector3 forward = target.forward;
        forward.y = 0;
        forward.Normalize();
        forward *= distance;

        Vector3 pos = target.position + forward;
        pos.y = target.position.y;
        return pos;
    }
}
